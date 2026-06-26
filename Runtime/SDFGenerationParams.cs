using System;
using UnityEngine;

namespace SDFImageKit
{
    /// <summary>How the field should be produced.</summary>
    public enum SDFGenerationBackend
    {
        /// <summary>Use the GPU (compute / jump-flood) when available, otherwise fall back to CPU.</summary>
        Auto = 0,
        /// <summary>Force the GPU jump-flooding path. Falls back to CPU if compute is unsupported.</summary>
        GPU = 1,
        /// <summary>Force the deterministic CPU path (8SSEDT). Works headless / on build machines.</summary>
        CPU = 2,
    }

    /// <summary>
    /// Immutable description of how to bake a signed distance field from a source alpha mask.
    /// </summary>
    [Serializable]
    public struct SDFGenerationParams : IEquatable<SDFGenerationParams>
    {
        [Tooltip("Output SDF resolution relative to the source texture. 1 = same size. " +
                 "0.25-0.5 is usually plenty and saves a lot of memory.")]
        [Range(0.05f, 1f)] public float resolutionScale;

        [Tooltip("How far the field reaches from the edge, as a fraction of the sprite's smaller " +
                 "side. This is the hard ceiling on outline/glow reach. 0.25 = effects can extend " +
                 "up to a quarter of the sprite. Larger = wider effects but lower precision.")]
        [Range(0.02f, 1f)] public float spread;

        [Tooltip("Alpha value of the source texture treated as the shape boundary.")]
        [Range(0.01f, 0.99f)] public float alphaThreshold;

        [Tooltip("Transparent border baked around the sprite, as a fraction of its smaller side, so " +
                 "outline/glow have room to render BEYOND the sprite's edges (not clipped). 0 = none.")]
        [Range(0f, 0.5f)] public float padding;

        [Tooltip("Which backend to use when baking.")]
        public SDFGenerationBackend backend;

        public static SDFGenerationParams Default => new SDFGenerationParams
        {
            resolutionScale = 0.5f,
            spread = 0.15f,
            padding = 0.15f,
            alphaThreshold = 0.5f,
            backend = SDFGenerationBackend.Auto,
        };

        public bool Equals(SDFGenerationParams other) =>
            resolutionScale == other.resolutionScale &&
            spread == other.spread &&
            padding == other.padding &&
            alphaThreshold == other.alphaThreshold &&
            backend == other.backend;

        public override bool Equals(object obj) => obj is SDFGenerationParams o && Equals(o);

        public override int GetHashCode() => unchecked(
            (resolutionScale.GetHashCode() * 397) ^
            (spread.GetHashCode() * 13) ^
            (padding.GetHashCode() * 17) ^
            (alphaThreshold.GetHashCode() * 31) ^
            (int)backend);
    }
}
