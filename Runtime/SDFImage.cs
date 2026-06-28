using System;
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

        // Fixed unit that effect sizes (outline/glow width, dilate, softness) are authored in — a
        // fraction of the sprite's smaller side. The field may be baked with a larger spread to fit a
        // big glow; effect params are scaled by ReferenceSpread/field.spread so sizes stay constant.
        internal const float ReferenceSpread = 0.15f;

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
        private float m_LastGlowKey;          // max glow width baked into the runtime field
        private bool m_HasResolvedOnce;
        private bool m_SettingsDirty = true;

        // Reused packing buffers (length = SDFShaderIDs.MaxEffects).
        private readonly Vector4[] m_FxColors = new Vector4[SDFShaderIDs.MaxEffects];
        private readonly Vector4[] m_FxParams = new Vector4[SDFShaderIDs.MaxEffects];
        private readonly float[] m_FxTypes = new float[SDFShaderIDs.MaxEffects];

        private static Material s_SharedDefault;

        // Global master switch + a registry of live instances so toggling refreshes them all at once.
        private static bool s_EffectsEnabled = true;
        private static readonly List<SDFImage> s_Instances = new List<SDFImage>();

        // ====================================================================================
        // Public API
        // ====================================================================================

        /// <summary>
        /// Global master switch for the whole package. When <c>false</c>, <b>every</b> <see cref="SDFImage"/>
        /// renders as a plain <see cref="UnityEngine.UI.Image"/> — no SDF material, no effects, no mesh
        /// expansion — while keeping each one's effect settings for when it's switched back on. Toggling
        /// refreshes all live instances immediately.
        /// </summary>
        public static bool EffectsEnabled
        {
            get => s_EffectsEnabled;
            set
            {
                if (s_EffectsEnabled == value) return;
                s_EffectsEnabled = value;
                for (int i = s_Instances.Count - 1; i >= 0; i--)
                {
                    var img = s_Instances[i];
                    if (img != null) img.MarkEffectsDirty();
                }
            }
        }

        /// <summary>
        /// The raw effect stack. <b>Index 0 is the front (top); the last item is the back.</b> Editing
        /// this list directly needs a follow-up <see cref="MarkEffectsDirty"/> — for most code prefer the
        /// helpers below (<see cref="GetEffect{T}"/>, <see cref="Modify{T}"/>, <see cref="SetEffectEnabled{T}"/>,
        /// <see cref="SetEffectColor{T}"/>, …), which refresh automatically.
        /// </summary>
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

        // ------------------------------------------------------------------------------------
        // Effect control. Everything here refreshes the render automatically — you never need to
        // call MarkEffectsDirty() yourself. Effects are addressed by type; methods named "…s" or
        // "All" act on every effect of that type (the stack may hold several outlines/glows/shadows).
        // ------------------------------------------------------------------------------------

        /// <summary>First effect of type <typeparamref name="T"/> in the stack, or <c>null</c>.</summary>
        public T GetEffect<T>() where T : SDFEffect
        {
            for (int i = 0; i < m_Stack.Count; i++)
                if (m_Stack[i] is T t) return t;
            return null;
        }

        /// <summary>True if an effect of type <typeparamref name="T"/> exists; outputs the first one.</summary>
        public bool TryGetEffect<T>(out T effect) where T : SDFEffect
        {
            effect = GetEffect<T>();
            return effect != null;
        }

        /// <summary>Every effect of type <typeparamref name="T"/> (front-to-back), as a new list.</summary>
        public List<T> GetEffects<T>() where T : SDFEffect
        {
            var list = new List<T>();
            for (int i = 0; i < m_Stack.Count; i++)
                if (m_Stack[i] is T t) list.Add(t);
            return list;
        }

        /// <summary>
        /// Edit the first effect of type <typeparamref name="T"/>, then refresh. No-op if none exists.
        /// Example: <c>sdf.Modify&lt;SDFOutlineEffect&gt;(o => { o.color = Color.red; o.width = 0.4f; });</c>
        /// </summary>
        public SDFImage Modify<T>(Action<T> edit) where T : SDFEffect
        {
            var fx = GetEffect<T>();
            if (fx != null && edit != null) { edit(fx); MarkEffectsDirty(); }
            return this;
        }

        /// <summary>Edit every effect of type <typeparamref name="T"/>, then refresh. No-op if none.</summary>
        public SDFImage ModifyAll<T>(Action<T> edit) where T : SDFEffect
        {
            bool any = false;
            if (edit != null)
                for (int i = 0; i < m_Stack.Count; i++)
                    if (m_Stack[i] is T t) { edit(t); any = true; }
            if (any) MarkEffectsDirty();
            return this;
        }

        /// <summary>Enable or disable the first effect of type <typeparamref name="T"/>.</summary>
        public SDFImage SetEffectEnabled<T>(bool enabled) where T : SDFEffect
            => Modify<T>(e => e.enabled = enabled);

        /// <summary>Enable or disable every effect of type <typeparamref name="T"/>.</summary>
        public SDFImage SetEffectsEnabled<T>(bool enabled) where T : SDFEffect
            => ModifyAll<T>(e => e.enabled = enabled);

        /// <summary>Enable or disable a specific effect instance.</summary>
        public SDFImage SetEffectEnabled(SDFEffect effect, bool enabled)
        {
            if (effect != null && effect.enabled != enabled) { effect.enabled = enabled; MarkEffectsDirty(); }
            return this;
        }

        /// <summary>Set the colour of the first effect of type <typeparamref name="T"/>.</summary>
        public SDFImage SetEffectColor<T>(Color color) where T : SDFEffect
            => Modify<T>(e => e.EffectColor = color);

        /// <summary>Set the colour of every effect of type <typeparamref name="T"/>.</summary>
        public SDFImage SetEffectsColor<T>(Color color) where T : SDFEffect
            => ModifyAll<T>(e => e.EffectColor = color);

        /// <summary>
        /// Set the sweep <see cref="SDFShineEffect.position"/> (0..1) of every Shine effect and refresh.
        /// This only updates the material (no mesh rebuild), so it's cheap to call every frame to animate
        /// a moving sheen, e.g. <c>sdf.SetShinePosition(Mathf.Repeat(Time.time * speed, 1f));</c>
        /// </summary>
        public SDFImage SetShinePosition(float position)
        {
            bool any = false;
            if (m_Stack != null)
                for (int i = 0; i < m_Stack.Count; i++)
                    if (m_Stack[i] is SDFShineEffect s) { s.position = position; any = true; }
            if (any) { m_SettingsDirty = true; SetMaterialDirty(); }   // material only — no mesh rebuild
            return this;
        }

        /// <summary>Add a new effect at the <b>front</b> (top) of the stack and refresh; returns it so
        /// you can keep configuring it (e.g. <c>sdf.AddEffect&lt;SDFGlowEffect&gt;().color = Color.cyan;</c>).</summary>
        public T AddEffect<T>() where T : SDFEffect, new()
        {
            var fx = new T();
            m_Stack.Insert(0, fx);
            MarkEffectsDirty();
            return fx;
        }

        /// <summary>Add an existing effect and refresh. By default it goes to the <b>back</b> (behind the
        /// others); pass <paramref name="front"/> = true to put it on top.</summary>
        public SDFImage AddEffect(SDFEffect effect, bool front = false)
        {
            if (effect != null)
            {
                if (front) m_Stack.Insert(0, effect); else m_Stack.Add(effect);
                MarkEffectsDirty();
            }
            return this;
        }

        /// <summary>Remove a specific effect. Returns true if it was in the stack.</summary>
        public bool RemoveEffect(SDFEffect effect)
        {
            if (effect != null && m_Stack.Remove(effect)) { MarkEffectsDirty(); return true; }
            return false;
        }

        /// <summary>Remove every effect of type <typeparamref name="T"/>. Returns how many were removed.</summary>
        public int RemoveEffects<T>() where T : SDFEffect
        {
            int removed = m_Stack.RemoveAll(e => e is T);
            if (removed > 0) MarkEffectsDirty();
            return removed;
        }

        /// <summary>Remove all effects.</summary>
        public SDFImage ClearEffects()
        {
            if (m_Stack.Count > 0) { m_Stack.Clear(); MarkEffectsDirty(); }
            return this;
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
            if (!s_Instances.Contains(this)) s_Instances.Add(this);
            ForceResolve();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            s_Instances.Remove(this);
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
            // When runtime generation is available the field grows to fit the glow (and a baked field
            // can fall back to a runtime one for a big glow), so re-resolve when the glow changes.
            float glowKey = m_GenerateAtRuntime ? MaxGlowWidth() : 0f;
            bool changed = !m_HasResolvedOnce
                || s != m_LastResolvedSprite
                || m_BakedData != m_LastBaked
                || m_GenerateAtRuntime != m_LastGenerate
                || !m_RuntimeParams.Equals(m_LastParams)
                || glowKey != m_LastGlowKey;

            if (changed)
            {
                ResolveData(s);
                m_LastResolvedSprite = s;
                m_LastBaked = m_BakedData;
                m_LastGenerate = m_GenerateAtRuntime;
                m_LastParams = m_RuntimeParams;
                m_LastGlowKey = glowKey;
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
            // Master switch off → behave exactly like a normal Image (default material, no effects).
            if (!s_EffectsEnabled) return base.GetModifiedMaterial(baseMaterial);

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

            // Master switch off → leave the plain Image mesh untouched (no padding/glow expansion).
            if (!s_EffectsEnabled) return;

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
            float blur = MaxBlurRadius();
            if ((glow > 0f || blur > 0f) && data.field != null)
            {
                float fw = Mathf.Max(1, data.field.width), fh = Mathf.Max(1, data.field.height);
                float contentW = Mathf.Max(1f, fw * (1f - 2f * pad.x));
                float contentH = Mathf.Max(1f, fh * (1f - 2f * pad.y));
                float minC = Mathf.Min(contentW, contentH);
                // Glow reach is absolute (width x ReferenceSpread); a blur softens out by ~its radius.
                // Both are fractions of the smaller side, so take the larger as the needed margin.
                float reachPix = Mathf.Max(glow * ReferenceSpread, blur) * minC;
                // +15% so the effect fully fades before the mesh edge (no hard clip).
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
                // Distance the largest glow needs the field to cover (absolute, in sprite fractions).
                float reach = MaxGlowWidth() * ReferenceSpread;
                bool bakedOk = m_BakedData != null && m_BakedData.IsValid;
                bool bakedCoversGlow = bakedOk && reach <= m_BakedData.spread + 1e-3f;

                if (bakedOk && bakedCoversGlow)
                {
                    // Fast path: the baked field already reaches far enough for the glow.
                    newData = m_BakedData;
                    fieldIsSource = true;
                }
                else if (m_GenerateAtRuntime && s.texture != null)
                {
                    // No baked field, OR the glow is bigger than the baked field can represent: generate
                    // a runtime field grown to actually contain the glow, so it stays shape-following
                    // instead of falling back to the (boxy) far-field extrapolation. spread = encoding
                    // range, padding = spatial coverage; both must reach the glow.
                    acqTex = s.texture;
                    acqParams = m_RuntimeParams;
                    float r = Mathf.Min(1f, reach * 1.05f);
                    if (r > acqParams.spread) acqParams.spread = r;
                    if (r > acqParams.padding) acqParams.padding = r;
                    newData = SDFRuntimeCache.Acquire(acqTex, acqParams);
                    acquired = newData != null;
                    fieldIsSource = false;
                }
                else if (bakedOk)
                {
                    // Glow exceeds the baked field but runtime generation is off — use the baked field;
                    // the shader extrapolates the overflow (re-embed with a larger Spread for an exact,
                    // shape-following large glow).
                    newData = m_BakedData;
                    fieldIsSource = true;
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
                mat.SetVector(SDFShaderIDs.SpriteRect, ComputeSpriteRect(sp));
            }

            // Effect sizes are authored in fixed reference units; the field may be baked with a larger
            // spread (e.g. to fit a big glow), so scale the distance params into the field's range.
            float distScale = (m_ActiveData != null && m_ActiveData.spread > 1e-4f)
                ? ReferenceSpread / m_ActiveData.spread : 1f;

            // Pack enabled layers back-to-front: index 0 = bottom of list = drawn first (back).
            int n = 0;
            if (m_Stack != null)
            {
                for (int i = m_Stack.Count - 1; i >= 0 && n < SDFShaderIDs.MaxEffects; i--)
                {
                    var fx = m_Stack[i];
                    if (fx == null || !fx.enabled) continue;
                    m_FxColors[n] = fx.EffectColor;
                    m_FxParams[n] = fx.PackedParamsScaled(distScale);
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

        /// <summary>
        /// The sprite's valid UV region in <c>_MainTex</c> (uMin, vMin, uMax, vMax). The whole [0,1] for
        /// a full-texture sprite; a sub-rect for a sprite packed into an atlas (or a multi-sprite sheet).
        /// The blur clamps its taps to this so it never samples neighbouring atlas sprites.
        /// </summary>
        private static Vector4 ComputeSpriteRect(Sprite s)
        {
            if (s == null || s.texture == null) return new Vector4(0f, 0f, 1f, 1f);
            Rect tr = s.textureRect;
            float tw = Mathf.Max(1, s.texture.width), th = Mathf.Max(1, s.texture.height);
            return new Vector4(tr.xMin / tw, tr.yMin / th, tr.xMax / tw, tr.yMax / th);
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

        /// <summary>Largest <see cref="SDFBlurEffect.radius"/> among enabled blurs (0 if none).</summary>
        private float MaxBlurRadius()
        {
            float m = 0f;
            if (m_Stack != null)
                for (int i = 0; i < m_Stack.Count; i++)
                    if (m_Stack[i] is SDFBlurEffect b && b.enabled && b.radius > m) m = b.radius;
            return m;
        }
    }
}
