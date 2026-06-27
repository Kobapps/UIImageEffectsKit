# Changelog

All notable changes to **UI Image Effects Kit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.0] - 2026-06-27

### Added
- **Blur effect** (`SDFBlurEffect`) in the stack — a soft-focus blur of the sprite itself (colour
  *and* alpha, so the edges soften too). `radius` is a fraction of the sprite, `strength` blends
  sharp↔blurred. It's a single-pass 32-tap golden-angle disk blur, weighted toward the centre and
  rotated per-fragment so it stays smooth (no ring artifacts) even on hard edges. Composites with the
  other effects — e.g. a blurred sprite under a crisp Outline for a frosted-card look. Repeatable; the
  rendered mesh auto-grows to fit the radius. Available from **+ Add Effect** and from code.

## [1.3.0] - 2026-06-27

### Added
- **Global master switch** for the whole package: `SDFImage.EffectsEnabled` (static). When `false`,
  **every** `SDFImage` renders as a plain `Image` — no SDF material, no effects, no mesh expansion —
  while keeping each one's effect settings. Toggling refreshes all live instances immediately. In the
  editor it's also exposed as a checkable **Tools ▸ UI Image Effects Kit ▸ Effects Enabled** menu item
  (persists via EditorPrefs across sessions and play-in-editor), and the inspector shows a re-enable
  banner while it's off. Builds use the static API.

## [1.2.0] - 2026-06-26

### Added
- **Effect-control API** on `SDFImage` for driving effects from code — type-addressed and
  auto-refreshing (no manual `MarkEffectsDirty()`):
  - Query: `GetEffect<T>()`, `TryGetEffect<T>(out e)`, `GetEffects<T>()`.
  - Edit: `Modify<T>(e => …)`, `ModifyAll<T>(e => …)`.
  - Toggle: `SetEffectEnabled<T>(bool)`, `SetEffectsEnabled<T>(bool)`, `SetEffectEnabled(effect, bool)`.
  - Colour: `SetEffectColor<T>(Color)`, `SetEffectsColor<T>(Color)` (`SDFEffect.EffectColor` is now settable).
  - Stack: `AddEffect(effect, front)`, `RemoveEffect(effect)`, `RemoveEffects<T>()`, `ClearEffects()`.

### Fixed
- README "From code" example had the effect order reversed (it added the Face last, putting it behind
  the outline/glow). Corrected: index `0` of `effects` is the front; `AddEffect<T>()` adds to the front,
  `AddEffect(instance)` to the back.

## [1.1.1] - 2026-06-26

### Fixed
- **Large glows now follow the sprite silhouette instead of turning into a square.** A glow wider than
  the baked field used to extrapolate the clamped distance field by distance-to-the-field-*box*, which
  read as a rounded square far out. Now the field is grown to actually contain the glow so it stays
  shape-following: a runtime-generated field auto-scales its spread/padding to the largest glow, and a
  sprite with an embedded field automatically falls back to a runtime field when a glow exceeds what the
  baked one can represent (turn off **Generate At Runtime** to keep the embedded field, or re-embed with
  a larger **Spread**). Effect sizes are now authored in a fixed reference unit, so they stay constant
  regardless of the field's baked spread. The residual extrapolation (for the no-runtime case) now falls
  off radially (a soft round halo) rather than as a box.

## [1.1.0] - 2026-06-26

### Added
- **Glow can now extend far beyond the sprite rect.** The glow `Width` range was raised (0–6× spread),
  the rendered mesh auto-expands to fit the largest glow in the stack, and the shader linearly
  extrapolates the clamped distance field past its baked range — so a wide glow renders a large, smooth
  halo well outside the RectTransform instead of being clipped, with **no re-bake required**. Nearby glow
  (within the baked field) is unchanged.

## [1.0.0] - 2026-06-26

### Added
- `SDFImage` — a drop-in component that extends `UnityEngine.UI.Image` and adds a
  reorderable **effect stack**: Face, Outline, Shadow / Underlay, and Glow. Outline,
  Shadow and Glow are repeatable (e.g. multiple outlines or glows); Face is a pinned
  singleton so the sprite always draws in front.
- Signed Distance Field rendering driven by a per-instance material and the
  `UI/SDF Image` shader, so effects stay crisp and resolution-independent at any zoom.
- **One-click SDF embedding**: the field is baked as sub-assets *inside the texture asset*
  (no companion file), via an `AssetPostprocessor`. Re-bakes automatically on reimport.
- **Runtime generation** fallback: GPU jump-flood compute when available, else a pure-C#
  8SSEDT CPU baker (works headless / on build machines with no GPU).
- Full uGUI integration: all Image draw modes (Simple / Sliced / Tiled / Filled),
  Multiple-sprite atlases, **`Mask` (stencil) and `RectMask2D`** masking, and edge
  padding so effects render beyond full-bleed sprite edges (e.g. 9-slice frames).
- UI Toolkit inspector with a draggable, layer-style effect stack.
- **Effect Showcase** sample (Package Manager ▸ Samples ▸ Import).

### Fixed
- SDF images now render correctly under a stencil `Mask`. uGUI clones the material via
  `StencilMaterial.Add` (`new Material(src)`), which drops `SetVectorArray` / `_FxCount`
  uniforms; the effect data is now re-applied to the wrapped material in
  `GetModifiedMaterial`, so masked SDF images match unmasked ones.

[1.4.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.4.0
[1.3.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.3.0
[1.2.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.2.0
[1.1.1]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.1.1
[1.1.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.1.0
[1.0.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.0.0
