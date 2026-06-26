using UnityEngine;

namespace SDFImageKit
{
    /// <summary>
    /// GPU signed-distance-field baker built on jump flooding (see SDFJumpFlood.compute).
    /// Much faster than the CPU path for large textures. Automatically unavailable when the
    /// platform has no compute support — callers should fall back to <see cref="SDFGeneratorCPU"/>.
    /// </summary>
    public static class SDFGeneratorGPU
    {
        private static ComputeShader s_Compute;

        /// <summary>True when this platform can run the GPU baker.</summary>
        public static bool IsSupported =>
            SystemInfo.supportsComputeShaders && LoadCompute() != null;

        private static ComputeShader LoadCompute()
        {
            if (s_Compute == null)
                s_Compute = Resources.Load<ComputeShader>("SDFJumpFlood");
            return s_Compute;
        }

        /// <summary>
        /// Bake a field on the GPU. Returns null if compute is unavailable so the caller can
        /// fall back to the CPU path.
        /// </summary>
        public static Texture2D Generate(Texture source, SDFGenerationParams p)
        {
            var cs = LoadCompute();
            if (cs == null || !SystemInfo.supportsComputeShaders || source == null)
                return null;

            int sw = source.width, sh = source.height;
            int w = Mathf.Max(1, Mathf.RoundToInt(sw * p.resolutionScale));
            int h = Mathf.Max(1, Mathf.RoundToInt(sh * p.resolutionScale));

            // ARGB32 copy of the source so the compute kernel can Load() it on any platform.
            var srcRT = RenderTexture.GetTemporary(sw, sh, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            srcRT.filterMode = FilterMode.Point;
            Graphics.Blit(source, srcRT);

            RenderTexture A = NewSeedRT(w, h);
            RenderTexture B = NewSeedRT(w, h);
            RenderTexture outRT = NewFieldRT(w, h);

            int kInit = cs.FindKernel("KInit");
            int kJump = cs.FindKernel("KJump");
            int kResolve = cs.FindKernel("KResolve");

            cs.SetInts("_Size", w, h);
            cs.SetInts("_SrcSize", sw, sh);
            cs.SetFloat("_Range", Mathf.Max(0.5f, p.spread * Mathf.Min(w, h)));
            cs.SetFloat("_Threshold", p.alphaThreshold);

            int gx = Mathf.CeilToInt(w / 8f);
            int gy = Mathf.CeilToInt(h / 8f);

            // Init seeds into A.
            cs.SetTexture(kInit, "_Source", srcRT);
            cs.SetTexture(kInit, "_Read", A);
            cs.Dispatch(kInit, gx, gy, 1);

            // Jump flood, ping-ponging A <-> B.
            RenderTexture read = A, write = B;
            for (int step = Mathf.Max(w, h) / 2; step >= 1; step >>= 1)
            {
                cs.SetInt("_Step", step);
                cs.SetTexture(kJump, "_Read", read);
                cs.SetTexture(kJump, "_Write", write);
                cs.Dispatch(kJump, gx, gy, 1);
                (read, write) = (write, read);
            }
            // Two extra unit passes (JFA+2) clean up the rare stray errors.
            for (int extra = 0; extra < 2; extra++)
            {
                cs.SetInt("_Step", 1);
                cs.SetTexture(kJump, "_Read", read);
                cs.SetTexture(kJump, "_Write", write);
                cs.Dispatch(kJump, gx, gy, 1);
                (read, write) = (write, read);
            }

            // Resolve signed distance from the final seed buffer (`read`).
            cs.SetTexture(kResolve, "_Read", read);
            cs.SetTexture(kResolve, "_Out", outRT);
            cs.Dispatch(kResolve, gx, gy, 1);

            Texture2D result = Readback(outRT, w, h);

            RenderTexture.ReleaseTemporary(srcRT);
            A.Release(); SafeDestroy(A);
            B.Release(); SafeDestroy(B);
            outRT.Release(); SafeDestroy(outRT);
            return result;
        }

        // Object.Destroy throws/errors in edit mode; pick the right call for the context.
        private static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        private static RenderTexture NewSeedRT(int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        private static RenderTexture NewFieldRT(int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        private static Texture2D Readback(RenderTexture rt, int w, int h)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "SDF_Field",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = prev;
            return tex;
        }
    }
}
