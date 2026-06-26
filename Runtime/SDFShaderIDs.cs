using UnityEngine;

namespace SDFImageKit
{
    /// <summary>
    /// Cached <see cref="Shader.PropertyToID"/> handles and keyword names shared by the runtime
    /// component and the "UI/SDF Image" shader. The shader renders a stack of effect layers packed
    /// into uniform arrays, so the C# and shader sides agree on the array property names here.
    /// </summary>
    public static class SDFShaderIDs
    {
        /// <summary>Maximum number of effect layers the shader can composite in one draw.</summary>
        public const int MaxEffects = 8;

        // Field
        public static readonly int SDFTex  = Shader.PropertyToID("_SDFTex");
        // (scaleX, scaleY, offsetX, offsetY) remap from sprite UV0 into the SDF texture.
        public static readonly int SDFRect = Shader.PropertyToID("_SDFRect");

        // Effect stack (parallel arrays, composited 0..count-1 back-to-front)
        public static readonly int FxColor  = Shader.PropertyToID("_FxColor");
        public static readonly int FxParams = Shader.PropertyToID("_FxParams");
        public static readonly int FxType   = Shader.PropertyToID("_FxType");
        public static readonly int FxCount  = Shader.PropertyToID("_FxCount");

        // Keyword: a usable field is bound (effects active). Without it the sprite renders plain.
        public const string KW_SDF = "SDFIMAGE_SDF";

        /// <summary>Canonical name of the shader shipped with this package.</summary>
        public const string ShaderName = "UI/SDF Image";
    }
}
