# Primitives

A **primitive** is a parametric building block. Each primitive type is one class implementing the primitive contract and registered in the `PrimitiveRegistry`. Placing a primitive creates a `PrimitiveInstanceData` (see `DATA_MODEL.md`). Adding a new primitive = new class + registration; **no data-model changes**.

## The contract

```csharp
public interface IPrimitive
{
    string   TypeId        { get; }   // "wall", "floor", "stairs", ...
    string   DisplayName   { get; }
    string   Category      { get; }   // palette grouping: Structure, Vertical, Curves

    // Parameter schema drives the inspector UI and supplies defaults.
    IReadOnlyList<ParamSpec> Parameters { get; }   // key, type, default, min/max, label

    // Named surfaces → one material each. Order is stable (drives surface index).
    IReadOnlyList<string> MaterialSlots { get; }    // e.g. ["Front","Back","Reveal"]

    // Build a multi-surface mesh: surface i ↔ MaterialSlots[i]. CCW, normals+tangents.
    ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx);

    // Collision for the baked StaticBody3D. Trimesh for static level geometry.
    Shape3D[] BuildCollision(PrimitiveInstanceData data, BuildContext ctx);
}
```

`BuildContext` carries storey height, grid cell size, and the `MaterialLibrary` (for slot→material resolution at bake time). `BuildMesh` is called both for live preview and for bake — it must be pure and deterministic.

### Mesh rules
- One **surface per material slot**, in `MaterialSlots` order. Empty surfaces are allowed (skip slot).
- CCW winding, outward normals. Generate normals + tangents (`SurfaceTool.GenerateNormals/Tangents`) so normal-mapped materials work in-game.
- Local space: build around the instance origin; the baker applies `LocalTransform` + storey elevation.
- UVs: world-ish scale (1 unit = 1 metre) so tiling materials look consistent across primitives. Document per-primitive UV intent if it deviates.

## Wall openings — box decomposition (load-bearing)

Door/window holes are **not** made by triangulating a polygon-with-hole. A wall segment is a box of length `L`, height `H`, thickness `T`. Each opening (offset `x`, width `w`, sill `s`, opening height `h`) splits the wall into **solid sub-boxes**:

```
elevation view of a wall with one window opening:

 ┌───────────────────────────────────────┐  ← H (storey/ wall height)
 │            HEADER block                │
 │        (above opening: s+h..H)         │
 ├──────────┬───────────────┬────────────┤  ← s + h
 │          │               │            │
 │  LEFT    │   (opening)    │   RIGHT    │
 │  block   │  reveals only  │   block    │
 │ (0..x)   │               │ (x+w..L)   │
 ├──────────┼───────────────┼────────────┤  ← s
 │          │  SILL block   │            │
 │  LEFT    │  (0..s)       │   RIGHT    │   (doors: s=0, no sill block)
 └──────────┴───────────────┴────────────┘  ← 0
```

- **LEFT** `[0, x]` and **RIGHT** `[x+w, L]` are full-height boxes.
- **SILL** `[x, x+w] × [0, s]` exists only for windows (`s>0`).
- **HEADER** `[x, x+w] × [s+h, H]`.
- **Reveals**: the four inner faces of the opening (left jamb, right jamb, sill top, header bottom) are quads on their own material slot (`Reveal`).

**Multiple openings (incl. stacked):** the front/back faces are swept in two passes. First split the wall into **vertical strips** at every opening edge (the unique `x` and `x+w` values, plus the wall ends). Then within each strip, take the openings that cover it, sort them by sill, and emit a solid quad for each **y-band** in the gaps (below the lowest, between consecutive openings, above the highest); add the top/bottom face for that strip unless an opening reaches the ceiling/floor there. Reveals are emitted per opening. This handles openings side by side **and stacked vertically** (a window above a door, two windows…) with no boolean ops, and for non-stacked walls emits exactly the old slab geometry. Openings may not overlap in 2D (offset×height); the edit tools and `CollectValid` enforce it. Collision uses the trimesh of the same geometry — cheap and exact.

> Never reach for CSG or polygon-hole triangulation here. The decomposition above is the whole feature.

## Opening ↔ wall lifecycle (load-bearing)

A door/window is an **`OpeningData` owned by a wall**, not a free instance — and (as of the
openings-as-objects work, see `PLANNED_OPENINGS_AS_OBJECTS.md`) it's a **first-class, selectable,
movable, resizable editor object**. While selected it shows as a solid coloured placeholder with the
wall drawn intact; deselecting "cuts" the real hole. The suppression is editor-only — bake/save always
build the real hole. The lifecycle:

- **Place** (`OpeningTool`, hotkeys `D`/`N`): click a wall face → `AddOpeningCommand` creates the
  `OpeningData` on that wall's **stable `Id`** at a snapped local **offset** along the wall.
- **Move / Resize** (`SelectTool` + gizmo, or the inspector): drag along the wall or edit
  offset/width/height/sill → `EditOpeningCommand`. Edits are snapped, clamped, and **overlap-rejected**
  (openings may not overlap in offset×height; `OpeningTool`/`CollectValid` enforce it). The wall
  re-decomposes around the new geometry.
- **Delete:** removing a selected opening → `RemoveOpeningCommand`. **Deleting a wall deletes its
  openings with it** — no orphaned openings.

Openings live on the wall (`PrimitiveInstanceData.Openings`), precisely so this lifecycle has one owner.

> **Frame trim is deferred.** `OpeningData` reserves `FrameType` + `FrameMaterialSlots` for real
> door/window moulding geometry, but no frame primitive generates it yet — today an opening is a bare
> hole (plus reveal quads on the wall's `Reveal` slot). The solid placeholder shown while editing is
> *not* the frame.

## Built-in primitives (current catalog)

Registered in `PrimitiveRegistry.CreateDefault()`. Each row's hotkey is the palette toggle / keyboard
shortcut (see `UI.md`). Openings (door/window) are **not** primitives — they're `OpeningData` on a
wall, placed via `OpeningTool` (see below).

| TypeId | Category | Key (hotkey) | Params | Material slots |
|--------|----------|:---:|--------|----------------|
| `floor` | Structure | `F` | width, depth, thickness | Top, Bottom, Edge |
| `wall` | Structure | `W` | length, height, thickness (+ openings) | Front, Back, Top, Ends, Reveal |
| `curved_wall` | Structure | `A` | radius, arc, height, thickness, segments | Front, Back, Top, Ends |
| `cylinder` | Structure | `L` | radius, height, sides | Side, Top, Bottom |
| `edge_curb` | Structure | `E` | width, depth, style (Rail/Elevated Rail/Bank), railHeight, thickness, bankAngle | Side, Top, Bottom |
| `ramp` | Vertical | `R` | length, rise, width | Surface, Side |
| `ramp_plane` | Vertical | `G` | length, rise, width, thickness | Surface, Side |
| `banked_curve` | Curves | `C` | radius, arc, width, bank, rise, thickness, segments | Surface, Side |
| `half_pipe` | Curves | `U` | length, radius, arc, curve, rise, deck, deckWidth, thickness, sides, segments | Surface, Side |
| `dome` | Curves | `O` | radius, height, convex, rings, sides | Surface, Bottom, Side |
| `path_sweep` | Curves | `P` | profile, width, thickness, bank, wallHeight, radius, arc, sides, segments, closed (+ a freeform control-point list) | Surface, Side |

This list is not a closed set — the registry is the extension point; adding a class + one
registration line adds a primitive (no data-model/serializer/baker changes).

## Path sweep — a cross-section swept along a freeform curve (load-bearing)

`path_sweep` is the unified "path tool": curved walls, banked roads, and half-pipe channels along an
**arbitrary 3D spline** rather than the fixed circular arc the other curve primitives use. It's the one
primitive whose geometry is driven by **variable-length data**, not just scalars.

- **Path data.** The control points live in `Parameters["points"]` as a `Godot.Collections.Array<Vector3>`
  (local space, relative to the instance origin), with a parallel `Parameters["banks"]`
  (`Array<float>`, degrees) holding a per-point bank. Both are variable-length and **must stay
  index-aligned** — every edit (move/insert/remove) touches them together. See `DATA_MODEL.md` for why
  this round-trips and how it's read back safely. The instance basis is identity; the path fully
  describes the shape.
- **Curve + frame.** The points are smoothed into a `Curve3D` (Catmull-Rom handles) and sampled into
  stations. Each station gets a **rotation-minimizing frame** (parallel-transport an up vector from the
  start), so the cross-section keeps its orientation through steep climbs and loops where a fixed
  world-up or a Frenet frame would twist or degenerate. `bank` (global) plus the per-point bank
  (interpolated by arc offset) roll the frame about the tangent.
- **Profiles.** `profile` picks the cross-section, each a closed 2D loop in frame coords (X = lateral,
  Y = up) swept as quad strips: `0` **Ribbon** (flat slab — bank it for a banked road), `1` **Channel**
  (concave U / half-pipe — `radius`/`arc`/`sides`), `2` **Wall** (upright guard wall — `wallHeight`),
  `3` **Tube** (a full circular pipe the ball rolls *inside* — for loops). Ribbon/Channel/Wall follow the
  same exterior-on-left winding as `HalfPipePrimitive`'s sweep; end caps triangulate the loop and orient
  each triangle by its own geometric normal (TriangulatePolygon's winding isn't guaranteed), so caps
  can't silently invert. **Tube is the exception**: a pipe cross-section is an annulus (a hole — not one
  simple polygon), so it has its own `SweepTube` (two concentric rings + annular end caps), not the loop
  sweep.
- **Closed loops.** `closed` (≥3 points) wraps the curve cyclically and the sweep seam-to-seam with no
  end caps. A parallel-transported frame doesn't return to its start orientation around a loop (a
  residual *holonomy* twist); the builder measures that residual about the closing tangent and unwinds
  it linearly along the ring so the seam joins untwisted. (Sanity: a flat horizontal ring has zero
  holonomy.)
- **Editing.** Unlike the axis-resize gizmos of other primitives, a selected path shows **per-point
  handles** — move in plan / move in height / bank / remove — plus a per-segment insert handle. See
  `UI.md`. `profile` and `closed` are edited from the inspector (`profile` as a labelled dropdown via
  `ParamSpec.Options`).
- **Collision** is the trimesh of the swept mesh — concavity preserved, so a ball rolls *inside*
  channels and loops.

## Adding a custom primitive

1. Implement `IPrimitive` (or extend a `PrimitiveBase` helper) in `Core/Primitives`.
2. Declare `Parameters` (drives the inspector) and `MaterialSlots`.
3. Implement `BuildMesh` (multi-surface) and `BuildCollision`.
4. Register it in `PrimitiveRegistry` (one line). It appears in the palette under its `Category`.
5. No data-model, serializer, or baker changes needed.

## Known-hard cases (don't over-build v1)

- **Wall corner joins.** v1: independent wall boxes that overlap slightly at corners (visually fine for most materials). "Proper" mitred joins need a wall-network footprint graph — **defer**.
- **Arbitrary floor shapes.** v1: axis-aligned rectangles. Arbitrary polygons via `Geometry2D.TriangulatePolygon` later.
- **Curved/rounded geometry** (rounded corners, gutters): build by revolve/extrude of a profile with a segment count param; keep segment counts modest for collision.
- **Collision** for static level geometry is **trimesh** (concave) per primitive, or convex sub-boxes where the decomposition gives them (walls). Don't generate per-triangle convex hulls.
