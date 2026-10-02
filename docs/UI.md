# UI — editor shell & panels

The editor UI is **native Godot `Control` nodes**, built entirely in code from `Source/App/Main.cs`
(`Main.tscn` is just a bare `Node3D` with the script). No `.tscn` UI scenes, no editor-side wiring.

> **Why native, not Avalonia/Estragonia:** this is a viewport-centric tool — the core interactions
> (draw, pick, gizmos, drag a texture onto a 3D object) are tightly coupled to the 3D view. Native
> Control nodes share Godot's input/focus/viewport system, so that coupling is free; an embedded
> Avalonia layer would put the panels on a separate input plane and force manual coordination. The
> one thing native costs us — rich data-bound inspectors — is recovered by generating fields from
> each primitive's `ParamSpec` (done: bool → checkbox, float/int → spinbox, in `InspectorPanel`).

## Layout

```
Control "UiRoot"  (full rect, Theme = UiTheme.Build() — the app-wide dark theme)
├─ VBoxContainer (shell)
│   ├─ MenuBarPanel            File / Edit / View / Help
│   ├─ VSplitContainer  (expands)
│   │   ├─ HSplitContainer                         ← top row
│   │   │   ├─ SceneTreePanel        (left dock,  Storey ▸ Primitive ▸ Opening)
│   │   │   └─ HSplitContainer
│   │   │       ├─ ViewportDropContainer  (3D view, expands)
│   │   │       └─ InspectorPanel     (right dock, selected object's properties)
│   │   └─ TabContainer  (bottom dock, min height UiConstants.BottomDockHeight)
│   │       ├─ PrimitivePalettePanel   "Primitives"  (draw tools, grouped by category)
│   │       ├─ TexturePalettePanel     "Textures"    (the texture library — drag sources)
│   │       └─ ProjectPanel            "Project"     (workspace, New/Open/Save, bake + export-to-game)
│   └─ StatusBar               active tool · draw height · selection count · active tool's how-to hint
├─ ToastLayer                  bottom-right notifications (MouseFilter Ignore — never blocks input)
└─ HelpOverlay                 F1 hotkey cheat sheet (hidden by default; click or Esc closes)
```

`TabContainer` uses each child's `Name` as the tab title.

### Theme, constants, factories

- **`UiTheme.Build()`** — the dark theme, built in code (no `.tres`) and set once on `UiRoot`;
  Godot propagates it down the whole Control branch. The SubViewport's rendered 3D image is
  unaffected (Theme only touches Control drawing — never set `Modulate` on the viewport container).
- **`UiConstants`** — dock sizes, margins, the accent color, toast colors. No per-panel magic numbers.
- **`UiFactory`** — shared `MakeButton` (FocusMode None), `Section`, `ApplyMargin`, `MakeFileDialog`,
  `ShortId`, and `ReleaseFocusOnSubmit` (SpinBoxes give keyboard focus back on Enter so the tool
  hotkeys keep working; clicking the 3D view also force-releases GUI focus, see ToolManager).

### Menu bar: click-only by design

Menu items show their shortcuts **cosmetically in the label** but do not register accelerators —
`ToolManager._UnhandledInput` stays the single owner of every contested key (Ctrl+S/Z/Y/B, Delete).
A real `PopupMenu` accelerator fires even while a SpinBox/LineEdit has focus, which would e.g.
delete the selected *object* while editing text. The one exception is **F1** (Help → Hotkey
Reference): nothing else owns it, so it is a real accelerator and works regardless of focus.

Edit → Undo / Redo / Delete are greyed out when unavailable — evaluated on the popup's `AboutToPopup`
(not on `EditorContext.Changed`, which fires per drag frame). View → Frame Selection (`.`) / Frame All
(`Home`) call `EditorCameraRig.FrameSelection/FrameAll`; the rig owns those keys itself (like `7`) and
gets world bounds from an injected `BoundsProvider` (`EditorContext.Bounds`, computed from BuildMesh
AABBs) so `Editor/Camera` stays free of session types. Ctrl+Shift+Z is a redo alias in `ToolManager`.

### Startup tool & status-bar hints

`ToolManager.Setup` ends by activating **Select**, so the first click in the viewport works. Every
tool, Select included, has a palette id (`"select"`, `"cut_hole"`, primitive TypeIds, `"door"`/`"window"`)
— `ActiveToolIdChanged` never carries null. Panels set up after `ToolManager` read `ActiveToolId` once
to sync, since the initial event fired before they subscribed. `ToolManager.HintFor(id)` is a one-line
how-to per tool (keep it in step with each tool's click flow); the StatusBar shows it on the right and
the palette uses it as the button tooltip.

### Notifications, dirty tracking, safety

- `EditorContext.Notified(NotifyLevel, msg)` (enum lives in `Editor/Session` so the context never
  references UI types) → Main subscribes → `ToastLayer.Show`. Save/bake/export results and
  missing-config warnings surface as toasts; `GD.Print` keeps the console mirror.
- `CommandStack.IsDirty` uses a **save-marker reference** (the command on top of the undo stack at
  the last save), so undoing back to the save point correctly returns to clean. `SaveSource`
  success calls `MarkSaved()`. Window title shows `LevelBuilder — <name>*`.
- `Main.ConfirmIfDirty(proceed)` — shared Save / Discard / Cancel dialog used by window close
  (`AutoAcceptQuit = false` + `NotificationWMCloseRequest`), the File menu and the Project tab's
  New/Open. Known accepted gap: document name + grid height-step edits bypass the command stack
  (metadata / view prefs) and never dirty.

## The 3D view lives in a SubViewport

So the docked panels can **shrink** the viewport instead of overlapping it, all 3D nodes (grid,
camera, view, gizmos, cursor, picker, tools, lights, environment) are children of a `SubViewport`
inside a `SubViewportContainer` (`Stretch = true`).

Load-bearing details:

- **`Stretch = true`** makes the SubViewport size track the container, which keeps the
  mouse→camera projection correct even though the viewport is offset by the side panels.
- **`RenderTargetUpdateMode = Always`** — the default `WhenVisible` can freeze a live viewport.
- **`OwnWorld3D` is left `false`** → the 3D content renders in the **inherited** main-window
  `World3D`. This matters for raycasting (see drag-drop below).
- **Input forwarding:** `SubViewportContainer` forwards mouse + keyboard into the SubViewport, so
  `EditorCameraRig`/`ToolManager`/`GridCursor` (which use `_UnhandledInput` + `GetViewport()`) work
  unchanged. Picking is a manual raycast from the SubViewport camera (`InstancePicker`), so it's
  consistent with the offset viewport for free.

### Gotcha: a focused `Tree`/`Button` steals tool hotkeys

Godot's `Tree` has incremental type-ahead search, and a focused `Button` grabs Space/Enter — either
would swallow the `F`/`W`/`S` tool hotkeys the instant the user clicks a panel. **Every panel widget
that doesn't need keyboard focus sets `FocusMode = None`** (the scene-tree `Tree`, every palette
`Button`). Mouse clicks still select/activate; the keys keep flowing to `ToolManager`.

## Panels

### SceneTreePanel (left)
The document hierarchy as a clickable `Tree`: **Storey ▸ Primitive ▸ Opening**. Two-way bound to
`EditorContext`: clicking a row calls `Select`/`SelectOpening`/`SetActiveStorey`; the editor's own
selection drives the highlight back. It listens to `EditorContext.Changed` (fires on *every* edit,
incl. each live-drag frame) and **gates a full rebuild on a cheap structural signature** (storey /
instance / opening ids + active storey) — selection-only changes just move the highlight via an
id→`TreeItem` map, so drags don't thrash the tree or reset expand/collapse state. A `_suppressSignal`
flag prevents the programmatic re-selection from echoing back as a user click.

### InspectorPanel (right)
Properties of the selected object, inside a vertical-only `ScrollContainer` (long param lists scroll;
width stays pinned by the dock's min size + autowrapped labels). Identity (registry DisplayName + id,
plus a "N selected" note for multi-selections); a **Texture** slot (hidden when nothing / an opening
is selected) (`TextureDropZone`)
showing the current texture and accepting a dropped swatch; **texture properties** (Tiling, Tint,
and Pixelate) for the current texture; and the selection's editable parameters (the primitive's
`ParamSpec`s, or an opening's offset/width/height/sill). Subscribes to `EditorContext.Changed`.

- **Texture properties (Tiling / Tint / Pixelate)** are persistent controls under the texture slot,
  shown only when the selection's primary slot points at a *texture* entry (a loaded `.material` has no
  editable props here). Pixelate is a toggle + a "Pixels" size spinner (longest side in texels) that
  only appears while the toggle is on. They edit the shared `MaterialEntry` via
  `EditorContext.EditMaterial` → `EditMaterialCommand` (undoable; affects every instance using that
  texture — see `DATA_MODEL.md`).
  Synced under `_suppress` like the parameter rows; being persistent (not rebuilt per selection) they
  avoid the QueueFree-inside-signal trap. Known minor: dragging the `ColorPickerButton` pushes one
  undo step per change.
- **Parameter rows** rebuild only when the selection *identity* changes (`_shownKey`); a same-selection
  `Changed` (a field's own edit, or a live gizmo drag) just pushes values into the existing `SpinBox`es
  via `_syncers`. All programmatic writes run under `_suppress` so they don't echo as new commands.

### PrimitivePalettePanel (bottom tab 1)
Every registered primitive **plus** the non-primitive tools — a **Tools** group (Select, Cut Hole) first,
and the two wall openings (door/window) — grouped by category (Tools → Structure → Openings → Vertical)
as toggle `Button`s in one `ButtonGroup`. Labels carry the hotkey (`Wall  (W)`); tooltips are the tool hint. Clicking one calls
`ToolManager.ActivateToolById(id)` — the *same* path as the keyboard hotkey. Two-way: `ToolManager`
fires `ActiveToolIdChanged(id|null)` from `SetActive`, the palette mirrors it via
`SetPressedNoSignal`. Note openings are **not** registry
primitives — they're `OpeningTool` presets, listed by id `"door"`/`"window"` which `ToolManager`
maps to those tools.

### TexturePalettePanel (bottom tab 2)
The texture library: a grid of `TextureSwatch` (drag sources), grouped by source, loaded by
`TextureCatalog.Load()` — the bundled Kenney prototype pack (grouped by color folder) plus any the
user has added (grouped `"custom"`). Drag sources only — no editor state.

**Add your own textures.** An **"Add texture…"** button opens a `FileDialog`
(`Access = Filesystem` so it browses the whole disk, `OpenFiles` for multi-select; png/jpg/jpeg/
webp/bmp/tga). `TextureCatalog.ImportUserTexture` **copies** each chosen file into
`res://Assets/user_textures/` (`DirAccess.CopyAbsolute`) so it gets a stable `res://` path that
save/bake can reference — an external OS path can't be referenced by a `.tscn`/`.tres`. The grid then
repopulates (`Populate()`), and user textures also reappear next session because `Load()` rescans the
folder.

**Why a just-added texture shows immediately — `TextureLoader`.** A PNG copied into the project at
runtime has no imported `.ctex` until the Godot editor reimports it, so `ResourceLoader` can't see it.
`Core/Data/TextureLoader.Load(path)` tries `ResourceLoader` first (imported = mipmaps/compression),
then falls back to `Image.LoadFromFile(ProjectSettings.GlobalizePath(path))` →
`ImageTexture.CreateFromImage`, which reads the raw file directly. Both `TextureSwatch` (the thumbnail)
and `MaterialResolver` (the applied material) route through it, so the swatch *and* the painted
geometry appear the same session, no reimport required. (Cached by path; re-adding a different file
under the same name shows the stale one until restart — minor, two caches to evict for a real fix.)
See `EXPORT.md` for the one bake-time caveat (baking before reimport embeds the texture inline).

### ProjectPanel (bottom tab 3)
Document-level actions, all routed through `EditorContext`/`AppConfig` (same paths as the hotkeys):

- **Workspace** — label + "Change…" (`FileDialog{OpenDir}`). Picking a folder persists it to
  `user://levelbuilder.cfg`, sets `Workspace.SetRoot` (creates `levels/` + `textures/`), and
  repopulates the texture palette (a `textures.Refresh` callback passed from `Main`).
- **Level** — a name `LineEdit` (the only focus-taking widget here; it edits `Document.Name`, which
  is metadata, not undo-tracked; commits on Enter or focus loss via `EditorContext.RenameDocument`, which fires
`Changed` so the window title follows) + **New** / **Open…** / **Save**. Open is a `FileDialog{OpenFile,
  *.tres}` rooted at `<workspace>/levels`; Save writes there and remembers the path for resume.
- **Bake (local preview)** — per-object and merged-chunk bakes into `<workspace>/baked` (no embed;
  needs a workspace set). See `EXPORT.md`.
- **Export to game** — target label + "Set…" (`FileDialog{OpenDir}`) + **Export to Game** (disabled
  until a target is set). Writes a merged chunk with inline-embedded textures into
  `<target>/levels/<Name>.tscn`. See `EXPORT.md`.

The panel keeps the name field in sync after New/Open by listening to `EditorContext.Changed` (and
only writing when the field isn't focused, so it never clobbers typing). Every button is
`FocusMode = None` to protect tool hotkeys.

## Drag-and-drop: applying textures

A "texture" is a `MaterialEntry` (see `DATA_MODEL.md`). Dropping one **paints every material slot**
of the target instance (whole-object) through an undoable `AssignMaterialCommand` —
`EditorContext.AssignTextureToInstance(id, texturePath)` ensures the library entry then assigns. The
library registration is intentionally **outside** undo (an append-only, id-deduped pool, like
`DefaultMaterials.Seed`); only the slot assignment is undoable.

Two drop targets:

- **InspectorPanel texture slot** — pure Control→Control via `TextureDropZone` (overrides
  `_CanDropData`/`_DropData`). Requires an object to be selected first.
- **The 3D viewport** — drag a swatch straight onto the object.

### Viewport drop — the four gotchas that had to be solved (in order)

Dropping onto a 3D object inside a `SubViewportContainer` is deceptively fiddly. The working
implementation is a transparent **overlay** (`ViewportDropOverlay : Control`, `MouseFilter = Pass`)
that fills the viewport area as a child of `ViewportDropContainer`:

1. **A bare `SubViewportContainer` may never get the drop.** It forwards the drag *into* the
   SubViewport, whose empty GUI swallows it. → Put the drop on a `Pass` overlay on top:
   `Pass` lets ordinary clicks/drags fall through to the container (orbit/draw unaffected) while the
   overlay still answers `_CanDropData`/`_DropData`.
2. **`World3D.DirectSpaceState` is `null` in a GUI callback.** Space queries are only valid during
   physics. → `_DropData` records `(position, texturePath)` and the raycast runs in `_PhysicsProcess`.
3. **`_PhysicsProcess` may not auto-enable on a code-created node.** → call `SetPhysicsProcess(true)`.
4. **The raycast must use the *inherited* world.** Because the SubViewport has `OwnWorld3D = false`,
   the bodies live in the main-window world. `_viewport.World3D` returns the subviewport's own
   **empty** world → every ray misses. Use **`_viewport.FindWorld3D().DirectSpaceState`**. (Everything
   else worked because `InstancePicker` is a `Node3D` and `GetWorld3D()` resolves to `FindWorld3D()`.)

The drop position is overlay-local; it's rescaled by `vp.Size / overlay.Size` for safety (1:1 in
practice). Mask is the body layer (1), same as the picker.

## Selection drag: click-vs-drag

`SelectTool` arms a move/resize `IEditHandle` on press and edits on drag; a plain click selects.
Two safeguards make this robust now that Controls border the viewport:

- **Poll the button, don't trust the release.** Releasing the mouse over a panel lets that panel
  consume the LMB-release, so `OnRelease` never reaches the viewport and the handle would stay armed
  (the object then follows the cursor). `UpdatePreview` checks `Input.IsMouseButtonPressed(Left)`
  first and commits/disarms the moment it's up.
- **6px drag deadzone.** A narrower viewport amplifies click-jitter into whole-cell snaps, so a
  select-click could jump the object. A press must travel >6px (screen space, via
  `InstancePicker.MouseScreen()`) before any handle moves.

### Path-sweep point handles
Most primitives expose axis-resize handles for scalar dimensions. A selected `path_sweep` instead shows
**per-control-point handles** (`Editor/Gizmos/Path*Handle`, all the same `IEditHandle` SelectTool drives):
move-in-plan (blue, X/Z), move-in-height (green, vertical), bank (orange, vertical drag → roll), and
remove (red, click); plus a per-segment insert (yellow, drag to add a point — including the closing
segment of a loop). Colours/sizes come from the optional `IStyledHandle` (default blue at full size).
Two specifics worth knowing: the **remove** handle commits on a plain *click* (`Changed => true`, since
the deadzone gates `Preview` but not `Commit`) and is offset to the side so it can't collapse onto the
move handles in top-down view (where a mis-click would silently delete); the point edits all snapshot
both `points` and `banks` into one `EditPathCommand` (see `DATA_MODEL.md`). `profile` and `closed` are
plain inspector params (`profile` renders as a dropdown — `ParamSpec.Options` → `OptionButton`).

## Conventions for new panels

- Build in code in `Main._Ready`; give each panel a `Setup(...)` called after it's in the tree
  (and after `EditorContext`/`ToolManager` exist, for ones that need them).
- React to document/selection changes via `EditorContext.Changed`; **gate** the work — that event
  fires per drag frame. Unsubscribe in `_ExitTree`.
- `FocusMode = None` on widgets that don't need keyboard focus, to protect tool hotkeys.
- Never mutate `*Data` from a panel — go through `EditorContext` → a command (undo/dirty-tracking).
```
