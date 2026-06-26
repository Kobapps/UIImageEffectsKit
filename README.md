# UI Image Effects Kit

**Crisp, resolution-independent outline · shadow · glow for uGUI — as a single drop-in component.**

`SDFImage` extends `UnityEngine.UI.Image` and adds a reorderable stack of effects driven by a
**Signed Distance Field** baked from the sprite's alpha. Because a distance field stores *how far
every pixel is from the shape's edge*, the shader can draw an outline (or shadow, or glow) that hugs
the real silhouette and stays razor-sharp at **any zoom** — no matter how far you scale the Image up.

It's a true drop-in: it keeps the native Image inspector, every draw mode, atlases, and both kinds of
UI masking working.

```
GameObject ▸ UI ▸ SDF Image   →   assign a sprite   →   Embed SDF   →   add effects
```

---

## Installation

Requires **Unity 2021.3 or newer** (developed and tested on Unity 6). The only dependency is the
built-in **uGUI** package (`com.unity.ugui`), which every project already has.

### Option A — Package Manager (Git URL) · recommended

1. Open **Window ▸ Package Manager**.
2. Click the **+** button (top-left) ▸ **Add package from git URL…**
3. Paste:

   ```
   https://github.com/Kobapps/UIImageEffectsKit.git
   ```

4. Press **Add**. Unity downloads and compiles the package.

To pin a specific version, append a tag:

```
https://github.com/Kobapps/UIImageEffectsKit.git#1.0.0
```

### Option B — edit `manifest.json`

Add the line below to `Packages/manifest.json` under `"dependencies"`:

```json
{
  "dependencies": {
    "com.kobapps.uiimageeffectskit": "https://github.com/Kobapps/UIImageEffectsKit.git"
  }
}
```

### Option C — clone into your project

Clone (or copy) this repository into your project's `Packages/` folder:

```
Packages/com.kobapps.uiimageeffectskit/
```

> **Samples:** after installing, open **Package Manager ▸ UI Image Effects Kit ▸ Samples** and
> **Import** the *Effect Showcase* to drop a grid of ready-made examples into your scene.

---

## Quick start

1. **Add the component.** `GameObject ▸ UI ▸ SDF Image`, or right-click an existing `Image` ▸
   **Convert to SDF Image**.
2. **Assign a sprite** exactly like a normal Image.
3. **Embed the field** (recommended): in the SDFImage inspector click **Embed SDF in Texture**, or
   right-click the texture ▸ **UI Image Effects Kit ▸ Embed SDF in Texture**. This bakes the field as
   **sub-assets nested inside the texture asset itself** — no extra file, zero runtime cost, and it
   re-bakes automatically whenever you edit the art. *Or* leave **Generate At Runtime** on and the
   field is built on first use (GPU, or CPU where no GPU is available).
4. **Add effects** in the **Effect Stack**: click **+ Add Effect** and pick Outline, Shadow /
   Underlay, Glow, or Face. Effects **stack and repeat** (e.g. two outlines, three glows) —
   **drag the ≡ handle to reorder** them like layers (top draws in front), toggle each on/off, and
   remove with ✕. The **Face is pinned to the front** (★) so the sprite always draws on top of its
   effects; it's the only singleton.

### From code

Build a stack and tweak it at runtime. Effects are addressed **by type**, and every helper refreshes
the render automatically — you never call `MarkEffectsDirty()` yourself.

```csharp
using SDFImageKit;
using UnityEngine;

var sdf = new GameObject("Icon", typeof(SDFImage)).GetComponent<SDFImage>();
sdf.sprite = mySprite;

// Build a stack. AddEffect<T>() adds on top (front); AddEffect(instance) adds behind (back).
sdf.AddEffect<SDFFaceEffect>();                                              // the sprite, on top
sdf.AddEffect(new SDFOutlineEffect { color = Color.white, width = 0.3f });   // behind the face
sdf.AddEffect(new SDFGlowEffect    { color = Color.cyan,  width = 1.2f });   // behind that

// --- control effects later, from anywhere ---
sdf.SetEffectEnabled<SDFGlowEffect>(false);          // turn the glow off
sdf.SetEffectColor<SDFOutlineEffect>(Color.red);     // recolour the outline
sdf.Modify<SDFGlowEffect>(g => { g.width = 2.5f; g.color = Color.magenta; }); // change any fields

// query / bulk ops
if (sdf.TryGetEffect<SDFOutlineEffect>(out var o)) o.color = Color.yellow;  // (then sdf.MarkEffectsDirty())
sdf.SetEffectsEnabled<SDFGlowEffect>(true);          // every glow (you can stack several)
sdf.RemoveEffects<SDFShadowEffect>();                // drop all shadows
```

**Effect-control API** (all auto-refresh, all chainable, all address effects by type):

| Method | Does |
|---|---|
| `GetEffect<T>()` / `TryGetEffect<T>(out e)` / `GetEffects<T>()` | find the first / any / all of a type |
| `Modify<T>(e => …)` / `ModifyAll<T>(e => …)` | edit the first / every effect of a type, then refresh |
| `SetEffectEnabled<T>(bool)` / `SetEffectsEnabled<T>(bool)` | toggle the first / every effect of a type |
| `SetEffectEnabled(effect, bool)` | toggle a specific instance |
| `SetEffectColor<T>(Color)` / `SetEffectsColor<T>(Color)` | recolour the first / every effect of a type |
| `AddEffect<T>()` / `AddEffect(effect, front)` | add on top / at a chosen end |
| `RemoveEffect(effect)` / `RemoveEffects<T>()` / `ClearEffects()` | remove one / all of a type / everything |

> **Order:** index `0` of `effects` is the **front** (top); the last item is the **back**. `AddEffect<T>()`
> inserts at the front, `AddEffect(instance)` appends at the back. The `effects` list is still public for
> full control — just call `MarkEffectsDirty()` after editing it directly.

---

## Effects

| Effect | Key parameters | Notes |
|---|---|---|
| **Face** | `mode` (Textured / Silhouette), `dilate`, `softness` | The sprite itself. *Textured* shows the real art (default); *Silhouette* ("Crisp Face") renders the SDF shape so it stays sharp at any zoom. Singleton, pinned to the front. |
| **Outline** | `color`, `width`, `softness` | A border that hugs the silhouette. Repeatable — stack two for an inline + outline. |
| **Shadow / Underlay** | `color`, `offset`, `softness`, `dilate` | A soft offset copy behind the face. Repeatable. |
| **Glow** | `color`, `width`, `power`, `inner` | An outward falloff. `power` shapes the curve; stack glows for neon. Repeatable. `width` can go past `1` to push the halo **well beyond the sprite rect**, and it stays **shape-following** (see below). |

All effect sizes are in a **fixed reference unit** (a fraction of the sprite's smaller side), so a `0.3`
outline looks identical at any zoom, display size, or field resolution — and independent of how the
field was baked.

A Glow's `width` can exceed `1` to reach far past the RectTransform. To keep a big glow hugging the
silhouette (rather than fading to a blob), the field must hold distance data that far out, so the kit
**grows the field to fit the glow automatically**: a runtime-generated field scales its spread/padding
to the largest glow, and a sprite with an **embedded** field falls back to a runtime field when a glow
is bigger than the baked one can represent. (Turn off **Generate At Runtime** to always keep the embedded
field — a glow beyond its baked range then falls off as a soft round halo — or re-embed the texture with
a larger **Spread** to bake the bigger range in.)

---

## Features

| Feature | How it's delivered |
|---|---|
| **Performant** | The field is baked once and stored as sub-assets **inside the texture asset**; no companion file, no per-frame distance work. |
| **Crisp** | Edges use screen-space derivatives of the field (`fwidth`), so they stay sharp at any zoom. |
| **All Image modes** | Simple / Sliced / Tiled / Filled all work — the component never touches mesh generation, it only swaps the material and adds a UV remap. |
| **Multiple sprites** | One field covers a whole source texture; each sub-sprite samples its own region. |
| **Seamless integration** | The native Image inspector is preserved; embedding is one click; the field auto-rebakes on reimport. |
| **Mask support** | Works under both `Mask` (stencil) and `RectMask2D` (clip-rect / ScrollRect); the shader carries the full uGUI stencil + soft-clip contract. |
| **Atlas support** | A per-sprite UV remap maps atlas UVs back into the baked source field. |
| **Runtime generation, all platforms** | `SDFGenerator` picks GPU jump-flood when available, else the CPU 8SSEDT path. |
| **GPU *and* CPU** | The CPU path is pure managed C# — works headless / on build machines with no GPU. |

---

## How it works

```
Runtime/
  SDFImage.cs            drop-in Image subclass; resolves the field, packs the effect stack into
                         shader arrays, injects a per-instance material via GetModifiedMaterial
  SDFEffect.cs           polymorphic, reorderable effect stack ([SerializeReference]):
                         Face / Outline / Shadow / Glow (all but Face are repeatable)
  SDFData.cs             baked field texture + metadata (embedded as a sub-asset)
  SDFGenerator*.cs       GPU (jump-flood compute) + CPU (8SSEDT) bakers behind one facade
  SDFRuntimeCache.cs     ref-counted cache so Images sharing a texture bake once
  Shaders/UI-SDFImage.shader   UI shader: SDF compositing + full mask/clip plumbing
  Resources/SDFJumpFlood.compute
Editor/
  SDFTextureEmbedder.cs  embeds the field as sub-assets INSIDE the texture during its import
  SDFAssetMenu.cs        texture context menu (embed / remove embedded SDF)
  SDFImageEditor.cs      UI Toolkit inspector: native Image UI + embed + draggable effect stack
  SDFImageMenu.cs        create + convert menus
  SDFShaderInclude.cs    keeps the shader Always-Included so builds resolve it
```

**Field encoding** — `value 0.5 = edge`, `> 0.5 inside`, `< 0.5 outside`. The normalized signed
distance is `(value − 0.5) · 2` in `[-1, 1]`, where `±1` is the field's **spread**. The CPU baker, the
GPU compute kernel, and the shader all share this convention.

---

## Notes & limitations

- **Effects follow the sprite's ALPHA channel, not the visible colour.** An SDF traces the alpha
  silhouette. If a sprite is an opaque card with art painted in RGB (common for game icons), the
  outline traces the *card*, not the art. For an outline that hugs the artwork, the sprite's alpha
  must match the artwork shape.
- **Mesh type must be Full Rect.** SDF effects draw in the area around the sprite, so a Tight mesh
  leaves no room. Embedding sets Full Rect automatically.
- **Effects extend BEYOND the sprite edges.** Baking adds a transparent border (the **Padding** import
  setting, default 15% of the sprite) and the component expands the rendered mesh into it, so
  outline / glow / shadow show right at the edges of full-bleed art (e.g. 9-slice frames). **Sliced**
  gives the most accurate edge effects; **Tiled** is best-effort. For effects larger than the padding,
  raise **Padding** when baking.
- **Atlas rotation:** keep **Allow Rotation off** for packed atlases — a baked source-space field
  can't be remapped onto a rotated atlas entry, so the component safely falls back to a plain Image
  there. Runtime-generated fields are immune (they cover the live atlas texture directly).

---

## License

[MIT](LICENSE.md) © 2026 Kobapps (Kobi Chariski)
