# CLAUDE.md

Operational guide for working in this repo. For *design* read `docs/ARCHITECTURE.md` and friends — this file stays focused on how to build, run, and not break things.

## What this is

**LevelBuilder** is a **standalone Godot 4.6 / C# application** for designing game levels out of parametric primitives (floors, walls, door/window frames, stairs, gutters, rounded corners, …) on a snapping 3D grid, then exporting them for use in another Godot game.

Two outputs per level:
- **Editable source** — a custom `Resource` saved as `.tres`. Re-openable in the builder.
- **Baked scene** — a `.tscn` `PackedScene` with merged `MeshInstance3D` geometry + `StaticBody3D` collision, dropped into the target game project.

Geometry is **procedural `ArrayMesh`** (no live CSG). Each primitive type is a class that generates its own mesh, collision, and named **material slots**, registered in a `PrimitiveRegistry`.

## Build & run

- **Build:** `dotnet build` from the repo root. (Verified working; SDK = Godot.NET.Sdk/4.6.3, target `net10.0`.) Godot also builds the C# assembly itself on play.
- **Run:** open the project in the Godot editor and press **F5** (main scene is `Scenes/Main.tscn`), or `godot --path .` if you have the Godot 4.6 binary on PATH. The user runs Godot — don't go hunting for the executable.
- **After changing C#:** rebuild (`dotnet build` or let Godot rebuild) before pressing play, or the editor runs stale code.

## Terminology (the word "level" is overloaded — use these names)

| Term | Meaning |
|------|---------|
| **Level** | The whole document/building being edited. One `.tres` source, one baked `.tscn`. |
| **Storey** | A vertical building layer (ground floor, 1st floor…). Has a base elevation + height. |
| **Primitive** | A parametric building block type (Wall, Floor, Stairs…). Defined once, placed many times. |
| **Primitive instance** | A placed primitive in a storey, with its own parameters + material assignments. |
| **Material slot** | A named surface on a primitive (e.g. Wall has `Front`, `Back`, `Reveal`) that maps to one material. |
| **Opening** | A hole in a wall (door/window). Owned by the wall; frames bind to it. |

"Floor is one level, walls are higher" from the original idea is **not** a separate concept — it's just primitive elevation within a storey.

## Where things live

Milestone 1 has scaffolded `Source/Core/{Data,Primitives,Geometry,Build}` and `Source/App/Main.cs` (the bare template's `Models/Main.cs` is gone). The `Editor/` and `UI/` trees below are still **target**, not present yet — create them as you implement (M2+).

```
Source/
  App/            bootstrap, Main, app-level wiring
  Core/
    Data/         LevelDocument, StoreyData, PrimitiveInstanceData, MaterialLibrary  (Resources)
    Primitives/   IPrimitive, PrimitiveRegistry, FloorPrimitive, WallPrimitive, ...
    Geometry/     MeshBuilder / SurfaceTool helpers, wall box-decomposition
    Grid/         grid model + snapping
    Build/        SceneBaker (.tscn), Exporter, save/load (.tres)
  Editor/
    Camera/       orbit/pan/zoom rig
    Tools/        ITool state machine: Select, FloorDraw, WallDraw, Place
    Commands/     undo/redo command stack
    Selection/    Gizmos/
  UI/             Toolbar, Palette, Inspector, StoreySelector, MaterialPicker
Scenes/           Main.tscn + UI scenes
Assets/           sample Materials/, Icons/
docs/             design docs (see below)
```

> NOTE: app code lives under `Source/` with namespaces mirroring folders (`LevelBuilder.Core.Data`, …). `Source/App/Main.cs` is the current entry point, attached to `Scenes/Main.tscn`. It's still the M1 bootstrap (round-trip + bake demo), not the real editor — M2 replaces its body with the editor shell.

## Conventions

- C# namespaces mirror folders under `LevelBuilder.*` (e.g. `LevelBuilder.Core.Primitives`). PascalCase types/methods, `_camelCase` private fields. See `docs/CONVENTIONS.md`.
- Data classes that get serialized are `[GlobalClass] partial class Foo : Resource` with `[Export]` properties. **Nested custom-Resource serialization in Godot C# has sharp edges** — see `docs/DATA_MODEL.md` and prove the round-trip early (Milestone 1).
- Geometry is built with `SurfaceTool`/`ArrayMesh`, one surface per material slot, CCW winding, generated normals/tangents. See `docs/PRIMITIVES.md`.
- Mutations to the level go through the command stack (`Editor/Commands`) so undo/redo and dirty-tracking stay correct. Don't mutate `*Data` resources directly from tools/UI.

## Load-bearing gotchas (read before touching these areas)

1. **Wall openings = box decomposition, not polygon-with-hole.** A wall with an opening splits into solid boxes (left, right, sill, header) + inner-reveal quads. Generalizes to N openings as vertical slabs. Do **not** triangulate a polygon-with-hole. (`docs/PRIMITIVES.md`)
2. **Frame↔wall lifecycle is explicit.** A frame binds to a stable wall ID + local offset; moving rebinds; **deleting a wall deletes/orphans its frames per the defined rule.** (`docs/PRIMITIVES.md`)
3. **Material swappability depends on stable baker output.** The baker must emit **deterministic, stable node names/paths**, and write the library material onto the **mesh surface** — leaving `surface_material_override` free for the consuming game. Unstable names silently break instance overrides on rebake. (`docs/EXPORT.md`)
4. **Build the spine before the breadth.** Milestone 1 = one Floor primitive through the *entire* pipeline (save `.tres` → load round-trip → bake `.tscn` → open in Godot) before adding more primitives. (`docs/ROADMAP.md`)

## Docs index

- `docs/ARCHITECTURE.md` — system overview, modules, data flow.
- `docs/DATA_MODEL.md` — the `Resource` graph, serialization rules & C# gotchas.
- `docs/PRIMITIVES.md` — the primitive contract, wall openings, frame binding, known-hard cases.
- `docs/EXPORT.md` — save/load + bake pipeline, material & node-naming rules.
- `docs/UI.md` — editor shell layout, panels, the SubViewport, and the drag-drop/texture gotchas.
- `docs/ROADMAP.md` — milestones, starting with the thin end-to-end slice.
- `docs/CONVENTIONS.md` — C#/Godot coding conventions.
