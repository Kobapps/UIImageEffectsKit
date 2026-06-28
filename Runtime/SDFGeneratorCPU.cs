using UnityEngine;

namespace SDFImageKit
{
    /// <summary>
    /// Deterministic, dependency-free CPU signed-distance-field baker using the classic
    /// 8SSEDT (8-points Signed Sequential Euclidean Distance Transform). Runs anywhere C# runs —
    /// including headless build machines and platforms without compute shaders.
    ///
    /// Output encoding (shared with the GPU path and the shader):
    ///   value 0.5 = edge, &gt;0.5 = inside, &lt;0.5 = outside,
    ///   signedDistanceInTexels = (value - 0.5) * 2 * distanceRange.
    /// </summary>
    public static class SDFGeneratorCPU
    {
        // Offset to the nearest feature cell. Stored as two int planes for cache friendliness.
        private const int Empty = 1 << 20; // "infinity" in texels (huge but won't overflow dx*dx+dy*dy)

        /// <summary>
        /// Bake a field from a source texture's alpha channel.
        /// </summary>
        /// <returns>A fresh RGBA32 <see cref="Texture2D"/> the caller owns.</returns>
        public static Texture2D Generate(Texture2D source, SDFGenerationParams p)
        {
            int sw, sh;
            Color32[] src = ReadPixels(source, out sw, out sh);
            return Generate(src, sw, sh, p);
        }

        /// <summary>Bake from raw, row-major (bottom-up) RGBA32 pixels.</summary>
        public static Texture2D Generate(Color32[] srcPixels, int sw, int sh, SDFGenerationParams p)
            => Generate(srcPixels, sw, sh, p, out _);

        /// <summary>
        /// Bake from raw pixels, also reporting the normalized transparent border that was added
        /// around the content (so effects can extend beyond the sprite edges).
        /// </summary>
        public static Texture2D Generate(Color32[] srcPixels, int sw, int sh, SDFGenerationParams p,
            out Vector2 paddingNorm)
        {
            int cw = Mathf.Max(1, Mathf.RoundToInt(sw * p.resolutionScale)); // content resolution
            int ch = Mathf.Max(1, Mathf.RoundToInt(sh * p.resolutionScale));
            int pad = Mathf.Max(0, Mathf.RoundToInt(Mathf.Clamp01(p.padding) * Mathf.Min(cw, ch)));
            int w = cw + 2 * pad;   // padded field resolution
            int h = ch + 2 * pad;
            paddingNorm = new Vector2((float)pad / w, (float)pad / h);

            // 1. Mask at padded resolution: content centred, transparent border counts as "outside".
            bool[] inside = new bool[w * h];
            BuildMask(srcPixels, sw, sh, cw, ch, pad, w, h, p.alphaThreshold, inside);

            // 2. Two distance transforms: one seeded by inside cells, one by outside cells.
            int[] inDx, inDy, outDx, outDy;
            BuildGrid(inside, w, h, true, out inDx, out inDy);
            BuildGrid(inside, w, h, false, out outDx, out outDy);

            EuclideanDistanceTransform(inDx, inDy, w, h);
            EuclideanDistanceTransform(outDx, outDy, w, h);

            // 3. Encode. Spread is relative to the CONTENT size (not the padded size) so effect sizes
            //    stay stable regardless of how much padding was added.
            float range = Mathf.Max(0.5f, p.spread * Mathf.Min(cw, ch));
            float inv = 1f / (2f * range);
            var outPixels = new Color32[w * h];
            for (int i = 0; i < w * h; i++)
            {
                float dIn = Mathf.Sqrt(inDx[i] * (float)inDx[i] + inDy[i] * (float)inDy[i]);
                float dOut = Mathf.Sqrt(outDx[i] * (float)outDx[i] + outDy[i] * (float)outDy[i]);
                float sd = dOut - dIn; // positive inside the shape, negative outside
                byte e = (byte)Mathf.Clamp(Mathf.RoundToInt((0.5f + sd * inv) * 255f), 0, 255);
                outPixels[i] = new Color32(e, e, e, e);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "SDF_Field",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(outPixels);
            tex.Apply(false, false);
            return tex;
        }

        // -------------------------------------------------------------------------------------

        // Fill an (w x h) padded grid: content [pad..pad+cw) x [pad..pad+ch) is box-sampled from the
        // source; everything else is "outside".
        private static void BuildMask(Color32[] src, int sw, int sh, int cw, int ch, int pad,
            int w, int h, float threshold, bool[] inside)
        {
            byte t = (byte)Mathf.Clamp(Mathf.RoundToInt(threshold * 255f), 0, 255);
            for (int y = 0; y < h; y++)
            {
                int cy = y - pad;
                bool rowIn = cy >= 0 && cy < ch;
                int sy0 = 0, sy1 = 0;
                if (rowIn) { sy0 = cy * sh / ch; sy1 = Mathf.Max(sy0 + 1, (cy + 1) * sh / ch); }
                for (int x = 0; x < w; x++)
                {
                    int cx = x - pad;
                    bool ins = false;
                    if (rowIn && cx >= 0 && cx < cw)
                    {
                        int sx0 = cx * sw / cw;
                        int sx1 = Mathf.Max(sx0 + 1, (cx + 1) * sw / cw);
                        int sum = 0, count = 0;
                        for (int sy = sy0; sy < sy1 && sy < sh; sy++)
                        {
                            int row = sy * sw;
                            for (int sx = sx0; sx < sx1 && sx < sw; sx++)
                            {
                                sum += src[row + sx].a;
                                count++;
                            }
                        }
                        byte avg = count > 0 ? (byte)(sum / count) : (byte)0;
                        ins = avg >= t;
                    }
                    inside[y * w + x] = ins;
                }
            }
        }

        private static void BuildGrid(bool[] inside, int w, int h, bool seedInside,
            out int[] dx, out int[] dy)
        {
            dx = new int[w * h];
            dy = new int[w * h];
            for (int i = 0; i < w * h; i++)
            {
                bool isFeature = inside[i] == seedInside;
                if (isFeature) { dx[i] = 0; dy[i] = 0; }
                else { dx[i] = Empty; dy[i] = Empty; }
            }
        }

        // Standard 8SSEDT double sweep.
        private static void EuclideanDistanceTransform(int[] dx, int[] dy, int w, int h)
        {
            // Pass 1: top-left -> bottom-right
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Compare(dx, dy, w, h, x, y, -1, 0);
                    Compare(dx, dy, w, h, x, y, 0, -1);
                    Compare(dx, dy, w, h, x, y, -1, -1);
                    Compare(dx, dy, w, h, x, y, 1, -1);
                }
                for (int x = w - 1; x >= 0; x--)
                    Compare(dx, dy, w, h, x, y, 1, 0);
            }
            // Pass 2: bottom-right -> top-left
            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    Compare(dx, dy, w, h, x, y, 1, 0);
                    Compare(dx, dy, w, h, x, y, 0, 1);
                    Compare(dx, dy, w, h, x, y, 1, 1);
                    Compare(dx, dy, w, h, x, y, -1, 1);
                }
                for (int x = 0; x < w; x++)
                    Compare(dx, dy, w, h, x, y, -1, 0);
            }
        }

        private static void Compare(int[] dx, int[] dy, int w, int h,
            int x, int y, int ox, int oy)
        {
            int nx = x + ox, ny = y + oy;
            if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;

            int ni = ny * w + nx;
            int cand_dx = dx[ni] + ox;
            int cand_dy = dy[ni] + oy;
            int i = y * w + x;
            long candDist = (long)cand_dx * cand_dx + (long)cand_dy * cand_dy;
            long curDist = (long)dx[i] * dx[i] + (long)dy[i] * dy[i];
            if (candDist < curDist)
            {
                dx[i] = cand_dx;
                dy[i] = cand_dy;
            }
        }

        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Extract a sub-rectangle from a row-major (bottom-up) RGBA32 pixel buffer. Used to bake a
        /// runtime field for one atlas sprite from its region of the shared atlas texture (instead of
        /// the whole atlas). <paramref name="rect"/> is in source pixels (Unity's bottom-left origin),
        /// clamped to the buffer.
        /// </summary>
        public static Color32[] CropPixels(Color32[] src, int srcW, int srcH, RectInt rect,
            out int outW, out int outH)
        {
            int x0 = Mathf.Clamp(rect.xMin, 0, srcW);
            int y0 = Mathf.Clamp(rect.yMin, 0, srcH);
            int x1 = Mathf.Clamp(rect.xMax, 0, srcW);
            int y1 = Mathf.Clamp(rect.yMax, 0, srcH);
            outW = Mathf.Max(1, x1 - x0);
            outH = Mathf.Max(1, y1 - y0);
            var dst = new Color32[outW * outH];
            for (int y = 0; y < outH; y++)
            {
                int srcRow = (y0 + y) * srcW + x0;
                int dstRow = y * outW;
                for (int x = 0; x < outW; x++)
                    dst[dstRow + x] = src[srcRow + x];
            }
            return dst;
        }

        /// <summary>
        /// Read a texture's pixels even when it is not marked Read/Write enabled, by blitting
        /// through a temporary RenderTexture. Works for atlas and compressed textures at runtime.
        /// </summary>
        public static Color32[] ReadPixels(Texture2D source, out int w, out int h)
        {
            if (source == null) { w = h = 0; return new Color32[0]; }

            if (source.isReadable)
            {
                w = source.width; h = source.height;
                return source.GetPixels32();
            }

            w = source.width; h = source.height;
            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tmp.Apply(false, false);
            var pixels = tmp.GetPixels32();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            if (Application.isPlaying) Object.Destroy(tmp); else Object.DestroyImmediate(tmp);
            return pixels;
        }
    }
}
