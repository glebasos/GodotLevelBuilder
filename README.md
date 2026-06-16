# LevelBuilder

> ⚠️ **This project was 100% vibe slopped.**

<img width="2560" height="1369" alt="Godot_v4 6 3-stable_mono_win64_q2hpHT46Dt" src="https://github.com/user-attachments/assets/094a7cd3-81d6-49fd-8052-2ed55c524244" />


A standalone **Godot 4.6 / C#** application for designing game levels out of parametric
primitives — floors, walls, ramps, stairs, curves, domes, doors and windows — on a snapping
3D grid, then **baking** them into a `.tscn` you drop straight into another Godot game.

Geometry is **procedural `ArrayMesh`** (no live CSG). Each primitive type generates its own
mesh, collision, and named material slots, and is registered in a `PrimitiveRegistry`.

## What you get

Every level produces two outputs:

- **Editable source** — a custom `Resource` saved as `.tres`. Re-openable in the builder.
- **Baked scene** — a `.tscn` `PackedScene` with merged `MeshInstance3D` geometry +
  `StaticBody3D` collision, ready for the target game project. Materials are written onto the
  **mesh surface** (not `surface_material_override`), leaving overrides free for the consuming game.

## Vocabulary

The word "level" is overloaded, so the project uses these names:

| Term | Meaning |
|------|---------|
| **Level** | The whole document/building being edited. One `.tres` source, one baked `.tscn`. |
| **Storey** | A vertical building layer (ground floor, 1st floor…). Has a base elevation + height. |
| **Primitive** | A parametric building-block type (Wall, Floor, Stairs…). Defined once, placed many times. |
| **Primitive instance** | A placed primitive in a storey, with its own parameters + materials. |
| **Material slot** | A named surface on a primitive (e.g. a Wall's `Front`, `Back`, `Reveal`). |
| **Opening** | A hole in a wall (door/window). Owned by the wall; selectable and movable. |

## Features

- **12 parametric primitives**, grouped by category:
  - **Structure** — Floor, Wall, Curved Wall, Cylinder, Edge Curb
  - **Vertical** — Ramp, Stairs, Ramp Plane, Stair Plane
  - **Curves** — Banked Curve, Half-Pipe, Dome / Bowl
- **Openings** — doors and windows as selectable, movable, resizable objects. The wall mesh and
  collision honour N openings via box decomposition (not polygon-with-hole). The hole "applies"
  on deselect and on bake/save.
- **Snapping 3D grid** with cell and corner snap modes (toggle with **Tab**).
- **Multi-storey** documents with per-storey elevation and height; navigate with **+ / −**.
- **Texture library** — a bundled Kenney prototype pack plus your own images. Apply by
  **drag-and-drop** onto a 3D object or the inspector's texture slot. Per-texture **tiling**,
  **tint**, and **pixelate** (downsample + nearest filter for a chunky pixel-art look).
- **Undo/redo** — every mutation goes through a command stack (`Z` / `Y`).
- **Live preview that matches the bake** — the editor view and the baker share one
  `MaterialResolver`, so what you see is what exports.
- **Export to game** — writes a merged chunk with inline-embedded textures into your target
  project, so the `.tscn` carries no `res://` dependency.

## Requirements

- **Godot 4.6** with the **.NET / C#** build (Mono build of the editor).
- **.NET SDK 10** (`net10.0`; the SDK is `Godot.NET.Sdk/4.6.3`).

## Build & run

```sh
# Build the C# assembly
dotnet build

# Then open the project in the Godot 4.6 editor and press F5
# (main scene is Scenes/Main.tscn)
```

Godot also builds the C# assembly itself when you press play. **After changing C#, rebuild
(`dotnet build`) or let Godot rebuild before pressing play**, or the editor runs stale code.

If you have the Godot 4.6 binary on your PATH, you can also launch with `godot --path .`.

## Using the editor

The editor shell is a single window: a left **scene-tree** dock, the central **3D viewport**, a
right **inspector**, and a bottom tab bar with **Primitives / Textures / Project** panels.

### Workflow

1. **Set a workspace** — *Project* tab → *Workspace → Change…*. This is where your `levels/`
   and `textures/` live; it's remembered between sessions.
2. **Pick a tool** — click a primitive in the *Primitives* tab, or press its hotkey.
3. **Draw** — click-drag on the grid to place the primitive at the active storey's elevation.
4. **Select & edit** — press **S**, click an object, and tweak its parameters in the inspector.
   Drag the gizmo to move/resize.
5. **Texture** — drag a swatch from the *Textures* tab onto the object (or the inspector slot).
6. **Save** the editable source (`Ctrl+S`), **bake** a local preview (`Ctrl+B`), or **Export to
   Game** from the *Project* tab.

### Camera

| Input | Action |
|-------|--------|
| Middle-mouse drag | Orbit around the focus point |
| Shift + middle-mouse drag | Pan the focus point |
| Mouse wheel | Zoom in / out |

### Keyboard

**Tools** (single key):

| Key | Tool | Key | Tool |
|-----|------|-----|------|
| `S` | Select | `T` | Stairs |
| `F` | Floor | `G` | Ramp Plane |
| `W` | Wall | `H` | Stair Plane |
| `D` | Door (opening) | `C` | Banked Curve |
| `N` | Window (opening) | `U` | Half-Pipe |
| `R` | Ramp | `E` | Edge Curb |
| `L` | Cylinder | `A` | Curved Wall |
| `O` | Dome / Bowl | | |

**Actions:**

| Key | Action |
|-----|--------|
| `Tab` | Toggle grid snap (cell ↔ corner) |
| `+` / `−` | Move up / down a storey |
| `Esc` | Cancel the current draw |
| `Delete` | Delete the selection |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo |
| `Ctrl+S` | Save source `.tres` |
| `Ctrl+B` | Bake to Godot (local preview) |

### The Project tab

The bottom **Project** tab holds every document-level action, in four sections:

#### Workspace

The **workspace** is a folder *on your disk* where your work lives — it is **not** inside this
app's Godot project (`res://` is read-only at runtime, so saved levels and added textures can't
go there). Picking one with **Change…** creates two subfolders and remembers the choice between
sessions:

- `<workspace>/levels/` — your editable `.tres` level sources (where Save/Open default).
- `<workspace>/textures/` — custom textures.

The label shows the current path, or *"(not set — pick a folder)"*. Set this first; Save and the
texture library depend on it.

#### Level

Create, open, and save the editable source `.tres`:

- **Name field** — the level's name (document metadata, not undo-tracked). It's the filename stem
  for Save/Bake/Export, and stays in sync when you New/Open (without clobbering what you're typing).
- **New** — start a fresh empty level.
- **Open…** — file dialog (`*.tres`) rooted at `<workspace>/levels/`.
- **Save** — writes `<workspace>/levels/<Name>.tres` and remembers the path (also `Ctrl+S`).

#### Bake (local preview)

Bakes a `.tscn` into this app's own project at `res://Baked/` — for previewing the result here,
**not** for shipping. Two modes:

- **Bake (per-object)** — one node per primitive instance; keeps per-object material overrides.
  Writes `res://Baked/<Name>.tscn` (also `Ctrl+B`).
- **Bake Merged Chunk** — one merged mesh per material + one precise trimesh collision; per-object
  overrides collapse to per-material. Writes `res://Baked/<Name>_merged.tscn`.

#### Export to game

Ships a level into another Godot project:

- **Set…** — choose the target game project's folder (remembered between sessions). The label
  shows it, or *"(no target project set)"*.
- **Export to Game** — disabled until a target is set. Writes a **merged chunk** with textures
  **embedded inline** into `<target>/levels/<Name>.tscn`, so the scene is fully self-contained
  (no `res://` setup needed in the target project).

### Adding your own textures

In the *Textures* tab, **Add texture…** copies an image (png/jpg/jpeg/webp/bmp/tga) into
`res://Assets/user_textures/` so it gets a stable `res://` path that save/bake can reference. It
appears immediately (raw-decoded via `TextureLoader`, no editor reimport needed) and reappears
next session.

## Project structure

```
Source/
  App/            bootstrap; Main.cs builds the entire editor UI in code
  Core/
    Data/         LevelDocument, StoreyData, PrimitiveInstanceData, MaterialLibrary (Resources)
    Primitives/   IPrimitive, PrimitiveRegistry + the 12 primitive types
    Geometry/     SurfaceTool/ArrayMesh helpers, wall box-decomposition
    Grid/         grid model + snapping
    Build/        SceneBaker (.tscn), MaterialResolver, save/load (.tres)
  Editor/
    Camera/       orbit/pan/zoom rig
    Tools/        ToolManager + tools (Select, draw tools, OpeningTool)
    Commands/     undo/redo command stack
    Gizmos/       move/resize edit handles
    Session/      EditorContext (the editor's central state + command facade)
  UI/             InspectorPanel, SceneTreePanel, PrimitivePalettePanel, TexturePalettePanel, ProjectPanel
Scenes/           Main.tscn (a bare Node3D + the Main script)
Assets/           sample Materials/, Icons/, bundled + user textures
docs/             design docs (see below)
```

C# namespaces mirror the folders under `LevelBuilder.*` (e.g. `LevelBuilder.Core.Primitives`).
Serialized data classes are `[GlobalClass] partial class Foo : Resource` with `[Export]`
properties — **one class per file**, filename matching the class, or `.tres` loads fail silently.

## Documentation

| Doc | Covers |
|-----|--------|
| `CLAUDE.md` | How to build, run, and not break things (the operational guide). |
| `docs/ARCHITECTURE.md` | System overview, modules, data flow. |
| `docs/DATA_MODEL.md` | The `Resource` graph, serialization rules & C# gotchas. |
| `docs/PRIMITIVES.md` | The primitive contract, wall openings, frame binding, hard cases. |
| `docs/EXPORT.md` | Save/load + bake pipeline, material & node-naming rules. |
| `docs/UI.md` | Editor shell layout, panels, the SubViewport, drag-drop gotchas. |
| `docs/ROADMAP.md` | Milestones and current status. |
| `docs/CONVENTIONS.md` | C#/Godot coding conventions. |

## Status

Milestones M1–M4 are done and F5-verified; primitive breadth and the texturing workflow are
largely in place. Known gaps include per-slot / set-for-type material assignment, a fully
`ParamSpec`-driven inspector, and arbitrary (non-rectangular) floor polygons. See
`docs/ROADMAP.md` for the live picture.
