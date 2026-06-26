# Changelog

All notable changes to **UI Image Effects Kit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.1.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.1.0
[1.0.0]: https://github.com/Kobapps/UIImageEffectsKit/releases/tag/1.0.0
