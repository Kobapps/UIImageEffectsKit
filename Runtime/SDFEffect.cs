using System;
using UnityEngine;

namespace SDFImageKit
{
    /// <summary>The kind of SDF effect, also used as the shader's per-layer type code.</summary>
    public enum SDFEffectKind
    {
        Face = 0,
        Outline = 1,
        Shadow = 2,
        Glow = 3,
        Blur = 4,
    }

    /// <summary>
    /// One layer in an <see cref="SDFImage"/>'s effect stack. Layers are composited bottom-to-top
    /// (bottom of the list = back). Stored polymorphically via <c>[SerializeReference]</c> so the
    /// stack can mix and repeat effect types.
    ///
    /// All sizes are fractions of the field's <c>spread</c> (so they're resolution/zoom-independent);
    /// shadow offset is a fraction of the sprite.
    /// </summary>
    [Serializable]
    public abstract class SDFEffect
    {
        [Tooltip("Include this layer in rendering.")]
        public bool enabled = true;

        /// <summary>Type of this effect (and the shader type code).</summary>
        public abstract SDFEffectKind Kind { get; }

        /// <summary>False for effects that only make sense once (e.g. the Face).</summary>
        public virtual bool AllowsMultiple => true;

        /// <summary>Label shown in the inspector stack.</summary>
        public virtual string DisplayName => Kind.ToString();

        /// <summary>Primary colour of this layer (its <c>color</c> / <c>tint</c> field). Get or set.</summary>
        public abstract Color EffectColor { get; set; }

        /// <summary>Four params packed for the shader; meaning depends on <see cref="Kind"/>.</summary>
        public abstract Vector4 PackedParams { get; }

        /// <summary>
        /// <see cref="PackedParams"/> with the <b>distance-like</b> components multiplied by
        /// <paramref name="k"/>. All effect sizes are authored in a fixed reference unit; the field
        /// may be baked with a different <c>spread</c> (e.g. enlarged to fit a big glow), so the
        /// component sends the same on-screen size whatever the field range. Components that are NOT
        /// distances (mode flags, exponents, sprite-relative offsets) are left untouched. Default
        /// scales nothing.
        /// </summary>
        public virtual Vector4 PackedParamsScaled(float k) => PackedParams;
    }

    /// <summary>The sprite itself. Singleton. Textured (real sprite) or SDF silhouette.</summary>
    [Serializable]
    public class SDFFaceEffect : SDFEffect
    {
        public enum FaceMode { Textured, Silhouette }

        [Tooltip("Tint multiplied onto the sprite.")]
        public Color tint = Color.white;
        [Tooltip("Textured = the real sprite (like a normal Image). " +
                 "Silhouette = SDF-driven alpha: crisp at any zoom, supports Dilate/Softness.")]
        public FaceMode mode = FaceMode.Textured;
        [Range(-1f, 1f), Tooltip("Silhouette only: grow (+) / shrink (-) the shape.")]
        public float dilate = 0f;
        [Range(0f, 1f), Tooltip("Silhouette only: edge softening / blur.")]
        public float softness = 0f;

        public override SDFEffectKind Kind => SDFEffectKind.Face;
        public override bool AllowsMultiple => false;
        public override Color EffectColor { get => tint; set => tint = value; }
        public override Vector4 PackedParams =>
            new Vector4(mode == FaceMode.Silhouette ? 1f : 0f, dilate, softness, 0f);
        public override Vector4 PackedParamsScaled(float k) =>
            new Vector4(mode == FaceMode.Silhouette ? 1f : 0f, dilate * k, softness * k, 0f);
    }

    /// <summary>A border that traces the alpha silhouette.</summary>
    [Serializable]
    public class SDFOutlineEffect : SDFEffect
    {
        public Color color = Color.black;
        [Range(0f, 1f), Tooltip("Thickness as a fraction of the field spread.")]
        public float width = 0.3f;
        [Range(0f, 1f)] public float softness = 0f;

        public override SDFEffectKind Kind => SDFEffectKind.Outline;
        public override Color EffectColor { get => color; set => color = value; }
        public override Vector4 PackedParams => new Vector4(width, softness, 0f, 0f);
        public override Vector4 PackedParamsScaled(float k) => new Vector4(width * k, softness * k, 0f, 0f);
    }

    /// <summary>An offset, softened drop shadow / underlay.</summary>
    [Serializable]
    public class SDFShadowEffect : SDFEffect
    {
        public Color color = new Color(0f, 0f, 0f, 0.5f);
        [Tooltip("Offset as a fraction of the sprite (x = right, y = up).")]
        public Vector2 offset = new Vector2(0f, -0.06f);
        [Range(0f, 1f)] public float softness = 0.1f;
        [Range(-1f, 1f), Tooltip("Grow (+) / shrink (-) the shadow shape.")]
        public float dilate = 0f;

        public override SDFEffectKind Kind => SDFEffectKind.Shadow;
        public override string DisplayName => "Shadow / Underlay";
        public override Color EffectColor { get => color; set => color = value; }
        public override Vector4 PackedParams => new Vector4(offset.x, offset.y, softness, dilate);
        // offset is a fraction of the sprite (not the spread), so it is NOT scaled.
        public override Vector4 PackedParamsScaled(float k) =>
            new Vector4(offset.x, offset.y, softness * k, dilate * k);
    }

    /// <summary>A soft outer (and optionally inner) glow.</summary>
    [Serializable]
    public class SDFGlowEffect : SDFEffect
    {
        public Color color = new Color(0.3f, 0.7f, 1f, 1f);
        [Range(0f, 6f), Tooltip("Outward reach, as a multiple of the field spread. Values above ~1 " +
                 "extend the glow beyond the baked field (and the sprite rect); the mesh and the " +
                 "distance field are extended automatically so the halo isn't clipped.")]
        public float width = 0.8f;
        [Range(0.1f, 8f), Tooltip("Falloff exponent. >1 = tighter near the edge.")]
        public float power = 1.5f;
        [Range(0f, 1f), Tooltip("Inward bleed from the edge.")]
        public float inner = 0f;

        public override SDFEffectKind Kind => SDFEffectKind.Glow;
        public override Color EffectColor { get => color; set => color = value; }
        public override Vector4 PackedParams => new Vector4(width, power, inner, 0f);
        // power is an exponent (not a distance), so it is NOT scaled.
        public override Vector4 PackedParamsScaled(float k) => new Vector4(width * k, power, inner * k, 0f);
    }

    /// <summary>
    /// A soft-focus blur of the sprite — a multi-tap blur of the sprite texture itself (not the SDF),
    /// so it blurs colour AND alpha (the edges soften too). Repeatable for a heavier blur.
    /// </summary>
    [Serializable]
    public class SDFBlurEffect : SDFEffect
    {
        [Tooltip("Tint multiplied onto the blurred sprite.")]
        public Color tint = Color.white;
        [Range(0f, 0.2f), Tooltip("Blur radius as a fraction of the sprite's smaller side. Larger " +
                 "radii are softer; very large values may show faint ringing (it's a single-pass blur).")]
        public float radius = 0.04f;
        [Range(0f, 1f), Tooltip("Blend from the sharp sprite (0) to fully blurred (1).")]
        public float strength = 1f;

        public override SDFEffectKind Kind => SDFEffectKind.Blur;
        public override Color EffectColor { get => tint; set => tint = value; }
        // radius is a fraction of the sprite (not the spread), so it is NOT scaled by the field range.
        public override Vector4 PackedParams => new Vector4(radius, strength, 0f, 0f);
    }

    /// <summary>Helpers shared by runtime + editor.</summary>
    public static class SDFEffectFactory
    {
        public static SDFEffect Create(SDFEffectKind kind)
        {
            switch (kind)
            {
                case SDFEffectKind.Face: return new SDFFaceEffect();
                case SDFEffectKind.Outline: return new SDFOutlineEffect();
                case SDFEffectKind.Shadow: return new SDFShadowEffect();
                case SDFEffectKind.Glow: return new SDFGlowEffect();
                case SDFEffectKind.Blur: return new SDFBlurEffect();
                default: return new SDFOutlineEffect();
            }
        }

        public static bool AllowsMultiple(SDFEffectKind kind)
        {
            // Mirror the instance rule without allocating.
            return kind != SDFEffectKind.Face;
        }
    }
}
