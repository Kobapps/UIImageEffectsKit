using UnityEngine;

namespace SDFImageKit
{
    /// <summary>
    /// Backend-agnostic entry point for baking signed distance fields. Picks GPU or CPU based on
    /// <see cref="SDFGenerationParams.backend"/> and platform capability, then wraps the result in
    /// an <see cref="SDFData"/>.
    /// </summary>
    public static class SDFGenerator
    {
        /// <summary>Bake just the field texture (caller owns it).</summary>
        public static Texture2D GenerateField(Texture source, SDFGenerationParams p)
            => GenerateField(source, p, out _);

        /// <summary>
        /// Bake the field and report the normalized padding border that was added. The GPU path does
        /// not pad, so when padding is requested the CPU path is used to honour it.
        /// </summary>
        public static Texture2D GenerateField(Texture source, SDFGenerationParams p, out Vector2 paddingNorm)
        {
            paddingNorm = Vector2.zero;
            if (source == null) return null;

            bool wantPadding = p.padding > 0f;
            bool wantGpu = !wantPadding &&
                           (p.backend == SDFGenerationBackend.GPU ||
                            (p.backend == SDFGenerationBackend.Auto && SDFGeneratorGPU.IsSupported));

            if (wantGpu)
            {
                var gpu = SDFGeneratorGPU.Generate(source, p); // GPU path never pads
                if (gpu != null) return gpu;
            }

            // CPU path needs CPU-side pixels; works for any Texture2D (readable or not).
            if (source is Texture2D tex2D)
            {
                var px = SDFGeneratorCPU.ReadPixels(tex2D, out int rw, out int rh);
                return SDFGeneratorCPU.Generate(px, rw, rh, p, out paddingNorm);
            }

            // Non-Texture2D source (e.g. RenderTexture): blit to a readable Texture2D first.
            int w = source.width, h = source.height;
            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tmp.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var result = SDFGeneratorCPU.Generate(tmp.GetPixels32(), w, h, p, out paddingNorm);
            if (Application.isPlaying) Object.Destroy(tmp); else Object.DestroyImmediate(tmp);
            return result;
        }

        /// <summary>
        /// Bake a field and wrap it in a runtime <see cref="SDFData"/> describing the whole source
        /// texture. The returned instance is owned by the caller (call <see cref="SDFData.DisposeRuntime"/>
        /// when done, unless handing it to the runtime cache).
        /// </summary>
        public static SDFData GenerateData(Texture source, SDFGenerationParams p)
            => GenerateData(source, p, null);

        /// <summary>
        /// Bake a field for a sprite's region of a texture. When <paramref name="sourceRect"/> is a
        /// sub-rect (an atlas-packed or sheet-sliced sprite), the field is baked from ONLY that region
        /// so it covers just the sprite, not the whole atlas — otherwise the sprite lands off-centre in
        /// a giant field (breaking shape-following glow) and wastes memory. <c>null</c> / the full rect
        /// bakes the whole texture (the original behaviour, GPU path allowed).
        /// </summary>
        public static SDFData GenerateData(Texture source, SDFGenerationParams p, RectInt? sourceRect)
        {
            if (source == null) return null;

            bool crop = sourceRect.HasValue &&
                (sourceRect.Value.x != 0 || sourceRect.Value.y != 0 ||
                 sourceRect.Value.width != source.width || sourceRect.Value.height != source.height);

            Texture2D field;
            Vector2 padNorm;
            Vector2Int srcSize;

            if (crop && source is Texture2D tex2D)
            {
                // Crop to the sprite's pixels, then bake on the CPU (we already need the pixels to crop,
                // and the glow fallback requests padding which the GPU path can't honour anyway).
                var full = SDFGeneratorCPU.ReadPixels(tex2D, out int fw, out int fh);
                var sub = SDFGeneratorCPU.CropPixels(full, fw, fh, sourceRect.Value, out int cw, out int ch);
                field = SDFGeneratorCPU.Generate(sub, cw, ch, p, out padNorm);
                srcSize = new Vector2Int(cw, ch);
            }
            else
            {
                field = GenerateField(source, p, out padNorm);
                srcSize = new Vector2Int(source.width, source.height);
            }
            if (field == null) return null;

            var data = ScriptableObject.CreateInstance<SDFData>();
            data.name = (source != null ? source.name : "SDF") + "_SDFData";
            data.field = field;
            data.spread = Mathf.Clamp(p.spread, 0.001f, 1f);
            data.sourceSize = srcSize;
            data.padding = padNorm;
            return data;
        }
    }
}
