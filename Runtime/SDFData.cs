using UnityEngine;

namespace SDFImageKit
{
    /// <summary>
    /// Baked signed distance field for one source texture. When baked in the editor this asset is
    /// nested inside the source texture (so it travels with it and never needs runtime work). It
    /// can also be created in memory at runtime by <see cref="SDFGenerator"/>.
    ///
    /// One <see cref="SDFData"/> covers an entire source texture, so every sprite sliced out of a
    /// "Multiple" sheet shares a single field; <see cref="SDFImage"/> selects the right region per
    /// sprite via a UV remap.
    /// </summary>
    public class SDFData : ScriptableObject
    {
        [Tooltip("The encoded distance field. 0.5 = edge, >0.5 = inside, <0.5 = outside.")]
        public Texture2D field;

        [Tooltip("Fraction of the sprite's smaller side that the field reaches (the +/- range " +
                 "mapped onto the encoded [0..1]). Effect sizes are expressed relative to this.")]
        public float spread = 0.25f;

        [Tooltip("Pixel size of the source texture this field was baked from. Used to map a " +
                 "sprite's pixel rect into normalized field coordinates.")]
        public Vector2Int sourceSize;

        [Tooltip("Normalized transparent border baked around the content on each side (x,y). The " +
                 "sprite content occupies field UV [padding .. 1-padding]; the rest is room for " +
                 "effects to extend beyond the sprite edges.")]
        public Vector2 padding;

        // --- Editor bookkeeping (ignored at runtime) ------------------------------------------
        [Tooltip("Editor only: GUID of the source texture this companion was baked from.")]
        public string sourceTextureGuid;
        [Tooltip("Editor only: parameters used to bake this field, so the importer can detect drift.")]
        public SDFGenerationParams bakeParams;
        [Tooltip("Editor only: hash of source content + params, used to skip unnecessary rebakes.")]
        public string sourceHash;

        /// <summary>True when the asset carries a usable field texture.</summary>
        public bool IsValid => field != null && sourceSize.x > 0 && sourceSize.y > 0;

        /// <summary>
        /// Frees the backing texture. Only call on runtime-generated instances you own — never on
        /// assets baked into a texture (the AssetDatabase owns those).
        /// </summary>
        public void DisposeRuntime()
        {
            if (field != null)
            {
                if (Application.isPlaying) Destroy(field);
                else DestroyImmediate(field);
                field = null;
            }
        }
    }
}
