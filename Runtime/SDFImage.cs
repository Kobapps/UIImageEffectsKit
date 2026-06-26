using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SDFImageKit
{
    /// <summary>
    /// Drop-in replacement for <see cref="UnityEngine.UI.Image"/> that renders a reorderable STACK
    /// of surface-correct effects — face, outline(s), shadow(s), glow(s) — using a cached Signed
    /// Distance Field. Effects stay crisp and consistent at any zoom or resolution.
    ///
    /// Because it derives from <see cref="Image"/> and never touches mesh generation, every Image
    /// feature keeps working unchanged: Simple / Sliced / Tiled / Filled draw modes, "Multiple"
    /// sprite sheets, sprite atlases, layout, raycasting, and being masked or acting as a mask.
    ///
    /// The field can be baked in the editor (cached companion asset) or generated on demand at
    /// runtime on any platform (GPU jump-flood, or CPU 8SSEDT as a universal fallback).
    /// </summary>
    [AddComponentMenu("UI/SDF Image", 11)]
    [RequireComponent(typeof(CanvasRenderer))]
    public class SDFImage : Image
    {
        [SerializeField] private SDFData m_BakedData;
        [Tooltip("Optional base material. Must use the \"UI/SDF Image\" shader. Leave empty to use " +
                 "the built-in default.")]
        [SerializeField] private Material m_SdfMaterial;

        // Polymorphic, reorderable effect stack. Bottom of the list renders at the back.
        [SerializeReference] private List<SDFEffect> m_Stack = new List<SDFEffect>();

        [Header("Runtime generation")]
        [Tooltip("If no baked field is available, generate one at runtime (cached and shared).")]
        [SerializeField] private bool m_GenerateAtRuntime = true;
        [SerializeField] private SDFGenerationParams m_RuntimeParams = SDFGenerationParams.Default;

        // --- runtime state ------------------------------------------------------------------
        private Material m_RenderMaterial;     // per-instance material we own
        private Material m_BaseForInstance;    // material the instance was cloned from
        private SDFData m_ActiveData;          // baked or runtime field currently in use
        private bool m_FieldIsSource;          // true => field covers original source (needs atlas remap)
        private bool m_RuntimeAcquired;        // true => we hold a ref in SDFRuntimeCache
        private Texture m_RuntimeSourceTex;    // texture we acquired runtime data for
        private SDFGenerationParams m_RuntimeAcquiredParams;
        private Sprite m_LastResolvedSprite;
        private SDFData m_LastBaked;
        private SDFGenerationParams m_LastParams;
        private bool m_LastGenerate;
        private bool m_HasResolvedOnce;
        private bool m_SettingsDirty = true;

        // Reused packing buffers (length = SDFShaderIDs.MaxEffects).
        private readonly Vector4[] m_FxColors = new Vector4[SDFShaderIDs.MaxEffects];
        private readonly Vector4[] m_FxParams = new Vector4[SDFShaderIDs.MaxEffects];
        private readonly float[] m_FxTypes = new float[SDFShaderIDs.MaxEffects];

        private static Material s_SharedDefault;

        // ====================================================================================
        // Public API
        // ====================================================================================

        /// <summary>The effect stack. Bottom of the list renders at the back. Mutate, then call
        /// <see cref="MarkEffectsDirty"/> to refresh.</summary>
        public List<SDFEffect> effects => m_Stack;

        public SDFData bakedData
        {
            get => m_BakedData;
            set { m_BakedData = value; MarkEffectsDirty(); }
        }

        public Material sdfMaterial
        {
            get => m_SdfMaterial;
            set { m_SdfMaterial = value; DestroyInstanceMaterial(); MarkEffectsDirty(); }
        }

        public bool generateAtRuntime
        {
            get => m_GenerateAtRuntime;
            set { m_GenerateAtRuntime = value; MarkEffectsDirty(); }
        }

        public SDFGenerationParams runtimeParams
        {
            get => m_RuntimeParams;
            set { m_RuntimeParams = value; MarkEffectsDirty(); }
        }

        /// <summary>Append an effect to the front of the stack and refresh.</summary>
        public T AddEffect<T>() where T : SDFEffect, new()
        {
            var fx = new T();
            m_Stack.Insert(0, fx);
            MarkEffectsDirty();
            return fx;
        }

        /// <summary>Mark the stack dirty so the next render refreshes the material.</summary>
        public void MarkEffectsDirty()
        {
            m_SettingsDirty = true;
            SetVerticesDirty();   // glow reach can change how far the mesh must expand
            SetMaterialDirty();
        }

        /// <summary>Throw away the resolved field and material so they rebuild on next render.</summary>
        public void ForceResolve()
        {
            ReleaseRuntimeData();
            m_LastResolvedSprite = null;
            m_ActiveData = null;
            m_HasResolvedOnce = false;
            m_SettingsDirty = true;
            SetAllDirty(); // padding affects the mesh too, so rebuild verts + material
        }

        // ====================================================================================
        // Lifecycle
        // ====================================================================================

        protected override void OnEnable()
        {
            base.OnEnable();
            ForceResolve();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ReleaseRuntimeData();
            DestroyInstanceMaterial();
        }

        protected override void OnDestroy()
        {
            ReleaseRuntimeData();
            DestroyInstanceMaterial();
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            // Start with just the sprite so a freshly added component looks like a normal Image.
            m_Stack = new List<SDFEffect> { new SDFFaceEffect() };
            ForceResolve();
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            MarkEffectsDirty();
        }
#endif

        protected override void OnDidApplyAnimationProperties()
        {
            base.OnDidApplyAnimationProperties();
            MarkEffectsDirty();
        }

        // ====================================================================================
        // Material pipeline
        // ====================================================================================

        private Sprite ActiveSprite => overrideSprite != null ? overrideSprite : sprite;

        private void EnsureDataResolved()
        {
            var s = ActiveSprite;
            bool changed = !m_HasResolvedOnce
                || s != m_LastResolvedSprite
                || m_BakedData != m_LastBaked
                || m_GenerateAtRuntime != m_LastGenerate
                || !m_RuntimeParams.Equals(m_LastParams);

            if (changed)
            {
                ResolveData(s);
                m_LastResolvedSprite = s;
                m_LastBaked = m_BakedData;
                m_LastGenerate = m_GenerateAtRuntime;
                m_LastParams = m_RuntimeParams;
                m_HasResolvedOnce = true;
                m_SettingsDirty = true;
            }
        }

        private void EnsureResolved()
        {
            EnsureDataResolved();
            EnsureInstanceMaterial();
            if (m_RenderMaterial != null && m_SettingsDirty)
            {
                ApplyToMaterial(m_RenderMaterial);
                m_SettingsDirty = false;
            }
        }

        /// <summary>
        /// Injected into <see cref="Graphic.materialForRendering"/>. Resolve the field, refresh our
        /// per-instance material, then defer to the base (which adds UI stencil/mask wrapping).
        /// </summary>
        public override Material GetModifiedMaterial(Material baseMaterial)
        {
            EnsureResolved();
            var mat = m_RenderMaterial != null ? m_RenderMaterial : baseMaterial;
            var result = base.GetModifiedMaterial(mat);

            // Under a stencil Mask (or any IMaterialModifier that wraps us), uGUI replaces our material
            // with a per-instance copy via StencilMaterial.Add, which does `new Material(src)`. That copy
            // carries the shader, enabled keywords and texture properties, but NOT uniform arrays set via
            // SetVectorArray/SetFloatArray, nor the `_FxCount` uniform (it isn't in the shader's
            // Properties block). Left alone, a masked SDFImage would run the SDF branch with an empty
            // effect stack (_FxCount = 0) and output nothing. Re-push the effect data onto the actual
            // material that gets rendered so masked SDF images look identical to unmasked ones.
            if (result != null && !ReferenceEquals(result, mat))
                ApplyToMaterial(result);

            return result;
        }

        /// <summary>
        /// Expand the generated mesh outward so effects can render BEYOND the sprite edges instead of
        /// being clipped: into the field's baked transparent padding, and — for glows — far enough to
        /// fit the glow's reach (which can exceed the sprite rect). Outer-boundary vertices are pushed
        /// out and their UV0 extrapolated; the shader remaps/extrapolates the field there. Works for
        /// Simple and Sliced.
        /// </summary>
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            base.OnPopulateMesh(vh);

            EnsureDataResolved();
            var data = m_ActiveData;
            if (data == null || !data.IsValid) return;
            Vector2 pad = data.padding;

            // How far to push the outer boundary outward, per axis, as a fraction of the content
            // mesh. The padding term reveals the field's baked transparent border; the glow term
            // lets a large glow extend past the sprite rect (the shader extrapolates the field there).
            float kx = pad.x > 0f ? pad.x / Mathf.Max(1e-4f, 1f - 2f * pad.x) : 0f;
            float ky = pad.y > 0f ? pad.y / Mathf.Max(1e-4f, 1f - 2f * pad.y) : 0f;
            float glow = MaxGlowWidth();
            if (glow > 0f && data.field != null)
            {
                float fw = Mathf.Max(1, data.field.width), fh = Mathf.Max(1, data.field.height);
                float contentW = Mathf.Max(1f, fw * (1f - 2f * pad.x));
                float contentH = Mathf.Max(1f, fh * (1f - 2f * pad.y));
                float spreadPix = Mathf.Max(0.5f, data.spread * Mathf.Min(contentW, contentH));
                float reachPix = glow * spreadPix;            // texels the glow reaches past the edge
                // +15% so the halo fully fades before the mesh edge (no hard clip).
                kx = Mathf.Max(kx, reachPix / contentW * 1.15f);
                ky = Mathf.Max(ky, reachPix / contentH * 1.15f);
            }
            // Cap so an extreme glow can't blow the mesh up unboundedly.
            kx = Mathf.Min(kx, 2.5f);
            ky = Mathf.Min(ky, 2.5f);
            if (kx <= 1e-5f && ky <= 1e-5f) return;

            int n = vh.currentVertCount;
            if (n == 0) return;

            var v = new UIVertex();
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                if (v.position.x < minX) minX = v.position.x;
                if (v.position.x > maxX) maxX = v.position.x;
                if (v.position.y < minY) minY = v.position.y;
                if (v.position.y > maxY) maxY = v.position.y;
                if (v.uv0.x < minU) minU = v.uv0.x;
                if (v.uv0.x > maxU) maxU = v.uv0.x;
                if (v.uv0.y < minV) minV = v.uv0.y;
                if (v.uv0.y > maxV) maxV = v.uv0.y;
            }

            float meshW = maxX - minX, meshH = maxY - minY;
            if (meshW <= 1e-4f || meshH <= 1e-4f) return;

            float padLX = meshW * kx, padLY = meshH * ky;
            float duvX = kx * (maxU - minU), duvY = ky * (maxV - minV);
            float ex = meshW * 0.002f + 1e-3f, ey = meshH * 0.002f + 1e-3f;

            for (int i = 0; i < n; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                Vector3 p = v.position;
                Vector4 uv = v.uv0;
                if (p.x <= minX + ex) { p.x -= padLX; uv.x -= duvX; }
                else if (p.x >= maxX - ex) { p.x += padLX; uv.x += duvX; }
                if (p.y <= minY + ey) { p.y -= padLY; uv.y -= duvY; }
                else if (p.y >= maxY - ey) { p.y += padLY; uv.y += duvY; }
                v.position = p;
                v.uv0 = uv;
                vh.SetUIVertex(v, i);
            }
        }

        private void ResolveData(Sprite s)
        {
            SDFData newData = null;
            bool fieldIsSource = false;
            bool acquired = false;
            Texture acqTex = null;
            SDFGenerationParams acqParams = default;

            if (s != null)
            {
                if (m_BakedData != null && m_BakedData.IsValid)
                {
                    newData = m_BakedData;
                    fieldIsSource = true;
                }
                else if (m_GenerateAtRuntime && s.texture != null)
                {
                    acqTex = s.texture;
                    acqParams = m_RuntimeParams;
                    newData = SDFRuntimeCache.Acquire(acqTex, acqParams);
                    acquired = newData != null;
                    fieldIsSource = false;
                }
            }

            ReleaseRuntimeData();

            m_ActiveData = newData;
            m_FieldIsSource = fieldIsSource;
            m_RuntimeAcquired = acquired;
            m_RuntimeSourceTex = acquired ? acqTex : null;
            m_RuntimeAcquiredParams = acqParams;
        }

        private void ReleaseRuntimeData()
        {
            if (m_RuntimeAcquired && m_RuntimeSourceTex != null)
                SDFRuntimeCache.Release(m_RuntimeSourceTex, m_RuntimeAcquiredParams);
            m_RuntimeAcquired = false;
            m_RuntimeSourceTex = null;
        }

        private void EnsureInstanceMaterial()
        {
            Material desiredBase = m_SdfMaterial != null ? m_SdfMaterial : SharedDefault;
            if (desiredBase == null) return;

            if (m_RenderMaterial != null && m_BaseForInstance == desiredBase)
                return;

            DestroyInstanceMaterial();
            m_RenderMaterial = new Material(desiredBase) { hideFlags = HideFlags.HideAndDontSave };
            m_BaseForInstance = desiredBase;
            m_SettingsDirty = true;
        }

        private void DestroyInstanceMaterial()
        {
            if (m_RenderMaterial != null)
            {
                if (Application.isPlaying) Destroy(m_RenderMaterial);
                else DestroyImmediate(m_RenderMaterial);
                m_RenderMaterial = null;
            }
            m_BaseForInstance = null;
        }

        private static Material SharedDefault
        {
            get
            {
                if (s_SharedDefault == null)
                {
                    var shader = Shader.Find(SDFShaderIDs.ShaderName);
                    if (shader != null)
                        s_SharedDefault = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                return s_SharedDefault;
            }
        }

        /// <summary>Resolve the field, pack the effect stack, and push it all onto a material.</summary>
        private void ApplyToMaterial(Material mat)
        {
            var sp = ActiveSprite;
            bool rotatedAtlasUnsupported = m_FieldIsSource && sp != null && sp.packed
                && sp.packingRotation != SpritePackingRotation.None;

            bool hasField = !rotatedAtlasUnsupported && m_ActiveData != null && m_ActiveData.IsValid;
            SetKeyword(mat, SDFShaderIDs.KW_SDF, hasField);

            if (hasField)
            {
                mat.SetTexture(SDFShaderIDs.SDFTex, m_ActiveData.field);
                mat.SetVector(SDFShaderIDs.SDFRect, ComputeSDFRect(sp));
                mat.SetVector(SDFShaderIDs.SDFExtend, ComputeSDFExtend());
            }

            // Pack enabled layers back-to-front: index 0 = bottom of list = drawn first (back).
            int n = 0;
            if (m_Stack != null)
            {
                for (int i = m_Stack.Count - 1; i >= 0 && n < SDFShaderIDs.MaxEffects; i--)
                {
                    var fx = m_Stack[i];
                    if (fx == null || !fx.enabled) continue;
                    m_FxColors[n] = fx.EffectColor;
                    m_FxParams[n] = fx.PackedParams;
                    m_FxTypes[n] = (int)fx.Kind;
                    n++;
                }
            }
            for (int k = n; k < SDFShaderIDs.MaxEffects; k++)
            {
                m_FxColors[k] = Vector4.zero;
                m_FxParams[k] = Vector4.zero;
                m_FxTypes[k] = 0f;
            }

            mat.SetVectorArray(SDFShaderIDs.FxColor, m_FxColors);
            mat.SetVectorArray(SDFShaderIDs.FxParams, m_FxParams);
            mat.SetFloatArray(SDFShaderIDs.FxType, m_FxTypes);
            mat.SetInt(SDFShaderIDs.FxCount, n);
        }

        private static void SetKeyword(Material mat, string keyword, bool on)
        {
            if (on) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
        }

        /// <summary>
        /// UV transform mapping the mesh's UV0 (sprite live-texture space) into the field's space.
        /// Identity for runtime fields and non-atlased sprites; a remap for baked fields whose
        /// sprite has been packed into an atlas.
        /// </summary>
        private Vector4 ComputeSDFRect(Sprite s)
        {
            // Base remap: sprite UV0 -> normalized content [0,1].
            float sx = 1f, sy = 1f, ox = 0f, oy = 0f;

            if (m_FieldIsSource && s != null && m_ActiveData != null && s.texture != null)
            {
                Texture tex = s.texture;
                float texW = tex.width, texH = tex.height;
                Rect tr = s.textureRect;
                Vector2 uv0Min = new Vector2(tr.xMin / texW, tr.yMin / texH);
                Vector2 uv0Max = new Vector2(tr.xMax / texW, tr.yMax / texH);

                Rect rr = s.rect;
                float srcW = Mathf.Max(1, m_ActiveData.sourceSize.x);
                float srcH = Mathf.Max(1, m_ActiveData.sourceSize.y);
                Vector2 fMin = new Vector2(rr.xMin / srcW, rr.yMin / srcH);
                Vector2 fMax = new Vector2(rr.xMax / srcW, rr.yMax / srcH);

                float dx = Mathf.Max(1e-6f, uv0Max.x - uv0Min.x);
                float dy = Mathf.Max(1e-6f, uv0Max.y - uv0Min.y);
                sx = (fMax.x - fMin.x) / dx; sy = (fMax.y - fMin.y) / dy;
                ox = fMin.x - uv0Min.x * sx; oy = fMin.y - uv0Min.y * sy;
            }

            // Then map content [0,1] -> padded field [padding .. 1-padding] so the baked transparent
            // border (where effects extend) lines up with the field's edges.
            Vector2 pad = m_ActiveData != null ? m_ActiveData.padding : Vector2.zero;
            float kx = 1f - 2f * pad.x, ky = 1f - 2f * pad.y;
            return new Vector4(sx * kx, sy * ky, ox * kx + pad.x, oy * ky + pad.y);
        }

        /// <summary>
        /// Per-axis factor converting "distance outside the SDF box" (in SDF-UV units) into the
        /// shader's normalized spread units, so a glow can extrapolate the clamped field beyond its
        /// baked range and extend past the sprite rect. = fieldSizeTexels / spreadTexels.
        /// </summary>
        private Vector4 ComputeSDFExtend()
        {
            var data = m_ActiveData;
            if (data == null || data.field == null) return Vector4.zero;
            float fw = Mathf.Max(1, data.field.width);
            float fh = Mathf.Max(1, data.field.height);
            Vector2 pad = data.padding;
            float contentW = fw * Mathf.Max(1e-4f, 1f - 2f * pad.x);
            float contentH = fh * Mathf.Max(1e-4f, 1f - 2f * pad.y);
            float spreadPix = Mathf.Max(0.5f, data.spread * Mathf.Min(contentW, contentH));
            return new Vector4(fw / spreadPix, fh / spreadPix, 0f, 0f);
        }

        /// <summary>Largest <see cref="SDFGlowEffect.width"/> among enabled glows (0 if none).</summary>
        private float MaxGlowWidth()
        {
            float m = 0f;
            if (m_Stack != null)
                for (int i = 0; i < m_Stack.Count; i++)
                    if (m_Stack[i] is SDFGlowEffect g && g.enabled && g.width > m) m = g.width;
            return m;
        }
    }
}
