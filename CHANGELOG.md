# Changelog

All notable changes to **UI Image Effects Kit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.6.4] - 2026-06-28

### Fixed
- **Effects on atlas-packed sprites now match the editor on device.** Several effects are measured in
  sprite space but were fed atlas-space coordinates once a sprite was packed into a Sprite Atlas — so
  they looked right in the editor (which renders the *un-packed* sprite) and broke on device (which
  renders the *packed* one):
  - **Glow / Shine filling the whole quad, or vanishing.** A glow larger than the baked field falls
    back to a runtime-generated field, which was baked from the *entire atlas texture* — leaving the
    sprite as a small off-centre region of a huge field (and wasting a lot of memory: a 4K atlas baked
    a ~33 MB field). The runtime field is now baked from **only the sprite's atlas region**, and the
    field↔mesh UV remap is applied to runtime fields too (previously only to baked *source* fields), so
    a packed sprite fills its field exactly as it does un-packed.
  - **Blur ring / edge artifacts.** The blur's sample radius (a fraction of the sprite) was applied in
    texture space, so on a packed sprite the taps overshot and all clamped onto the sprite-rect edges.
    The radius is now scaled by the sprite's atlas-UV size.
  - **Shine sweeping off the sprite** (no sheen visible) — it used raw texture UVs; it now uses
    sprite-local UVs.
  - **Stray atlas fragments / a faint box** around glows: the expanded effect mesh sampled neighbouring
    atlas sprites. The sprite-texture read is now clamped to the sprite's own UV rect, and the field is
    sampled clamped so the expanded margin always reads as "outside".
  - **Exaggerated shadow offset** on packed sprites — the offset (and glow centre/extent and crisp-blur
    feather) now use the field's content scale instead of the atlas remap scale.

## [1.6.3] - 2026-06-28

### Fixed
- **No effects in built players (all platforms, incl. Android) — the main mobile-effects bug.** The SDF
  path is selected by the `SDFIMAGE_SDF` shader keyword, which the component enables at runtime on a
  per-instance material. It was declared `shader_feature`, and `shader_feature` variants that no material
  *asset* references are **stripped from player builds** — so devices only had the "off" variant and drew
  the plain sprite (no effects, plus stray fragments from the expanded mesh), while the editor (which
  compiles variants on demand) looked correct. Changed to `multi_compile`, so both variants always ship.

## [1.6.2] - 2026-06-28

### Fixed
- **Blur on atlased sprites** no longer bleeds in the neighbouring sprites — the blur clamps its taps to
  the sprite's own UV region (`_SpriteRect`), so a packed atlas sprite can't sample its neighbours.

### Docs
- Documented the **Sprite Atlas** requirements for SDF effect sprites — **Allow Rotation OFF** and **Tight
  Packing OFF** — and the "fine in the editor, broken on device" cause: Sprite Atlas V2 packs at build
  time only, so the editor renders the un-packed sprite while the player renders the packed one.

## [1.6.1] - 2026-06-28

### Fixed
- **Mobile (Android / OpenGL ES) smudging.** On GPUs where `half` is real 16-bit float, two paths lost
  precision (invisible in the editor, which runs `half` as full float): the blur summed its 64 taps in
  `half`, and the RectMask2D clip-rect coordinates were carried in `half`. Both now use full `float`. No
  change on desktop. (The core SDF distance/coverage math was already full-precision.)

## [1.6.0] - 2026-06-27

### Added
- **Shine effect** (`SDFShineEffect`) — a sweeping sheen / gloss band across the sprite, clipped to its
  silhouette and drawn on top of the Face. Controls: `color`, `position`, `angle`, `width`, `softness`.
  **Animate `position` 0→1** for a moving shine; `SDFImage.SetShinePosition(float)` updates only the
  material (no mesh rebuild), so it's cheap to call every frame. Repeatable. The inspector treats Shine as
  an overlay — it auto-orders in front of the Face (which still stays in front of everything else).

## [1.5.1] - 2026-06-27

### Fixed
- **+ Add Effect** menu: the Shadow entry no longer opens an unwanted submenu. Its label contained a
  `/` ("Shadow / Underlay"), which `GenericMenu` treats as a submenu separator; it's now a single item
  ("Shadow (Underlay)").

## [1.5.0] - 2026-06-27

### Added
- **Blur `Crisp Edge` option** — an SDF/blur hybrid. The interior blurs as usual, but the silhouette's
  alpha is taken from the SDF feather, so the shape's edge stays perfectly clean while the contents go
  soft (a frosted-glass-*shape* look). Best for solid-silhouette icons; the feather is isotropic across
  any aspect ratio.

### Changed
- The Blur now averages **premultiplied** colour, so edges no longer pick up a dark fringe — a small
  quality bump for the normal blur too.

## [1.4.1] - 2026-06-27

### Changed
- **Smoother Blur.** The 1.4.0 blur used a per-fragment rotation to hide tap structure, which read as
  grain. Replaced it with a denser **64-tap** kernel that stays smooth on its own (no dither, no grain),
  and tightened the radius slider to the clean range (0–0.12).

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

[1.6.4]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.6.4
[1.6.3]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.6.3
[1.6.2]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.6.2
[1.6.1]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.6.1
[1.6.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.6.0
[1.5.1]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.5.1
[1.5.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.5.0
[1.4.1]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.4.1
[1.4.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.4.0
[1.3.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.3.0
[1.2.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.2.0
[1.1.1]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.1.1
[1.1.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.1.0
[1.0.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.0.0
