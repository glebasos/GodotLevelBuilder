# Improvement Plan (audit 2026-07-06)

A full-codebase audit (~13.5k lines, 136 C# files) and a phased, executable plan. Written so a
future session can implement it without re-deriving context. Work the phases **in order** —
Phase 1's tests are the safety net for Phase 2/3's refactors.

## Audit verdict

The architecture is healthy and the code is unusually well-documented. Do **not** rewrite:

- Clean layering: `Core` (engine-independent-ish data + geometry) / `Editor` (tools, commands,
  gizmos) / `UI` (panels). Namespaces mirror folders.
- All mutations go through `CommandStack` with reference-marker dirty tracking (correct after
  undo-past-save). Handles commit exactly one command per drag.
- Preview == bake: `LevelView` and `SceneBaker` share `IPrimitive.BuildMesh` + `MaterialResolver`.
- Hard-won gotchas are encoded in comments (ResourceIo temp-file dance, PortableCompressedTexture
  buffer keep, SubViewport world/focus traps, UV conventions). Preserve these comments.

The real gaps, in impact order: **(1) zero tests, (2) full-scene rebuild every frame during drags,
(3) an 835-line `EditorContext` god object with hardcoded type-string checks, (4) copy-pasted
helpers across all 17 primitives, (5) unbounded undo + assorted robustness nits.**

## Invariants — do not break these while refactoring

1. **Baked node names are API.** `{Type}_{id}` / `{Type}Shape_{id}_{s}` / `Mesh_{materialId}` are
   what lets consumer material overrides survive rebake (`docs/EXPORT.md`). Never rename.
2. **Materials go on the mesh SURFACE**, never `surface_material_override` (stays free for the game).
3. **Positional surface→slot convention.** Surface *i* of a built mesh maps to `MaterialSlots[i]`.
   Trailing surfaces are conditional (wall `Reveal` only with openings; path-sweep `Rail` only when
   railed) — legal only because they're trailing. See Phase 4.4 for the guard.
4. **Enum params are append-only** (e.g. `path_sweep.rail`) — stored ints in old `.tres` files keep
   their meaning.
5. **`ResourceIo`'s user:// staging dance** is load-bearing for external saves (script refs get
   dropped otherwise). Keep the mechanism; Phase 4.3 only hardens it.
6. **MeshBuilder winding/UV conventions** (CCW-from-normal corners, V-flip, world-unit UVs) are
   verified across 17 primitives. Don't "fix" them.
7. **Mutations only via commands** — never poke `*Data` resources from tools/UI directly.

---

## Phase 0 — Hygiene (½ hour, zero risk)

**0.1 Fix the git index.** `Source/Editor/Commands/EditPathPointsCommand.cs` is staged-as-added but
deleted on disk (`AD` in `git status`). Run `git rm --cached Source/Editor/Commands/EditPathPointsCommand.cs`
and commit together with whatever else lands first.

**0.2 Fix doc drift.**
- `CLAUDE.md` says SDK `Godot.NET.Sdk/4.6.3`; `LevelBuilder.csproj` is `4.7.0`. Update CLAUDE.md.
- `CLAUDE.md` "Where things live" still says `Editor/` and `UI/` are "target, not present yet" —
  they've existed for weeks. Rewrite that section from the actual tree.
- `docs/ROADMAP.md` checkboxes for M1–M5 items are unchecked though the status paragraph says done.
  Check off what's done (round-trip, baker, palettes, inspector, openings-as-objects…).

**0.3 csproj polish.** Add to the `PropertyGroup`:
`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (build is currently 0-warning — lock it in)
and `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`. Do **not** flip `<Nullable>enable</Nullable>`
globally yet — 136 files of churn; adopt `#nullable enable` per-file as files get touched in
Phase 3, and enable globally as a final step once the count is high.

## Phase 1 — Test infrastructure (the single biggest gap)

There are zero tests. `Core` is where the value is: geometry, serialization, bake determinism.

**1.1 Two-tier test setup.**
- **Tier A — plain xunit, no engine.** New project `Tests/LevelBuilder.Tests.csproj` referencing
  the main project. Godot's C# **structs** (`Vector3`, `Transform3D`, `Aabb`) work without a running
  engine, so anything that avoids `GodotObject`-derived types is testable here: `GizmoMath`,
  `Snapper`, `WallPrimitive.CollectValid`-style logic, `PathSweepPrimitive.BankAt`. Some of these are
  `private` — promote to `internal` + `[assembly: InternalsVisibleTo("LevelBuilder.Tests")]`.
  Note: the main csproj uses the Godot SDK; if the test project can't reference it cleanly, extract
  the pure math into a small `LevelBuilder.Core.Math`-style classlib — but try direct reference first.
- **Tier B — in-engine tests via [GdUnit4](https://github.com/MikeSchulze/gdUnit4) (C# API,
  runs headless: `godot --headless`).** This covers everything touching `Resource`, `SurfaceTool`,
  `ArrayMesh`, `ResourceSaver`. The user runs the Godot binary; wire the command but let them run it
  (see memory note: don't hunt for the executable).

**1.2 Priority tests (write these before any refactor):**
1. **Save/load round-trip** (Tier B): build a document with every primitive type + a wall with 2
   openings + textures with tiling/tint → save `.tres` → load with `CacheMode.Ignore` → compare via
   the existing `Source/Core/Build/LevelDocumentComparer.cs`. This is the M1 gate, still unautomated.
2. **Mesh sanity per primitive** (Tier B): for each registry primitive with default params —
   `BuildMesh` returns ≥1 surface, `GetSurfaceCount() <= MaterialSlots.Count`, no NaN vertices,
   `GetFaces().Length % 3 == 0`. Then a param-sweep (min/default/max of every `ParamSpec`) asserting
   no exception and no NaN — this catches degenerate-geometry regressions cheaply across all 17 types.
3. **Wall box decomposition** (Tier A if extractable, else B): openings side-by-side, stacked,
   full-height, edge-overlapping-rejected; face count matches expectation.
4. **Bake determinism** (Tier B): bake the same doc twice → identical node name sets; rename nothing.
5. **Undo/redo inverse** (Tier B): for each command type, Do→Undo restores a
   `LevelDocumentComparer`-equal document; Do→Undo→Redo equals Do.
6. **CommandStack dirty-marker semantics** (Tier A — it's engine-free): edit→save→undo→dirty,
   undo-to-marker→clean, undo-then-new-edit→dirty even at same depth.

**1.3 CI.** GitHub Actions: `dotnet build` + Tier A on push. Tier B optionally via
`chickensoft-games/setup-godot` with `--headless`; if flaky on CI, keep Tier B as a local
pre-release step and CI just builds + runs Tier A.

## Phase 2 — Performance: stop rebuilding the world every frame

**The problem.** `EditorContext.Refresh()` (`EditorContext.cs:129`) runs on *every live-drag frame*
(`SelectTool.UpdatePreview` → `_ctx.Refresh()`, `SelectTool.cs:116`). `LevelView.Rebuild()`
(`LevelView.cs:47`) then `QueueFree`s **every** child and rebuilds **every** instance in **every**
storey: `BuildMesh` once for the visual, then `BuildPickBody` → `prim.BuildCollision` → **a second
full `BuildMesh`** + `CreateTrimeshShape` (physics cooking) per instance. So one dragged floor in a
300-instance level costs ~600 mesh builds + 300 trimesh cooks *per frame*, plus a frame-long
QueueFree backlog. UI panels already gate on signatures; the 3D view is the unguarded hotspot.

**2.1 Per-instance node cache + signature diff in `LevelView`.**
- Keep `Dictionary<string, InstanceNodes>` (`record InstanceNodes(MeshInstance3D Mesh,
  StaticBody3D PickBody, List<StaticBody3D> OpeningBodies, string Signature)`) keyed by instance id.
- `Rebuild()` becomes a diff: compute per-instance signature = hash of (`PrimitiveType`,
  `LocalTransform`, `Parameters` content, `MaterialSlots` content, `Openings` content, storey
  `BaseElevation`, storey `Height`). Rebuild only instances whose signature changed; free nodes whose
  id vanished; add new ids. A cheap way to hash the Godot dictionaries: accumulate
  `VariantUtils`/`GD.Hash`-style hashes or build a small canonical string — measure, don't guess.
- Selection highlight and opening-placeholder changes must **not** count as geometry changes:
  apply `MaterialOverlay` toggles directly to cached `MeshInstance3D`s. The one exception is the
  selected-opening intact-wall view (`WithoutOpening`, `LevelView.cs:75`) — fold `SelectedOpeningId`
  for that wall into its signature so the wall rebuilds exactly when opening-selection state changes.
- Why signature-diff instead of plumbing dirty-ids through every command: no command API change,
  undo/redo/document-swap correctness falls out for free, and O(n) hashing is orders of magnitude
  cheaper than O(n) mesh builds. If profiling later shows hashing itself hurts at 1000+ instances,
  add an explicit dirty-id fast path on top.

**2.2 Build collision from the already-built mesh.** In `LevelView`, replace
`prim.BuildCollision(inst, ctx)` for pick bodies with `mesh.CreateTrimeshShape()` on the visual mesh
already in hand (they're identical today — every primitive's `BuildCollision` is
`BuildMesh().CreateTrimeshShape()`). Keep `IPrimitive.BuildCollision` for the baker. Optionally add a
default interface implementation `Shape3D[] BuildCollision(...) => new[]{ BuildMesh(data, ctx).CreateTrimeshShape() }`
and delete the 17 identical overrides.

**2.3 Defer collision cooking during drags.** While a handle drag is live, update only the dragged
instance's **visual** mesh; rebuild its pick collider and gizmo colliders once on release/cancel
(picking mid-drag isn't needed — `SelectTool` holds `_active`). Simplest wiring: give `Refresh()` an
optional `RefreshScope` (`Full` | `DragFrame`) and have `SelectTool.UpdatePreview` pass `DragFrame`;
`LevelView` skips collider rebuilds and `GizmoLayer` re-poses existing widgets instead of
rebuild-from-scratch in that scope. Commit/cancel then does a `Full` refresh.

**2.4 Index instance lookup.** `EditorContext.Find(id)` (`EditorContext.cs:659`) linear-scans all
storeys and is called several times per frame during drags (`OffsetOfInstance`, `GetInstance`).
After 2.1 this matters less, but it's trivial: maintain a `Dictionary<string,(StoreyData,int)>`
rebuilt lazily when a structural-change counter bumps (commands already all route through `Refresh`).

**2.5 Stress fixture + measurement.** Debug-only generator (hidden hotkey or Project-tab button):
place N×N floors+walls (e.g. 400 instances). Acceptance for the phase: with 400 instances, dragging
one instance stays at display refresh rate and allocates no per-frame trimesh; document-open and
undo still render correctly (that's what the Phase 1 tests guard).

## Phase 3 — Architecture: pay down the growth debt

**3.1 Split `EditorContext` (835 lines) into focused parts.** Keep `EditorContext` as the façade the
tools already use (don't churn 30 call sites), but move the logic into owned components:
- `SelectionModel` — `_selectedIds`, `SelectedOpeningId`, `SelectedPathPoint`, `SelectedHole`,
  clamp logic (`ClampPathSelection`), all `Select*/Toggle/Clear` methods. Fires its own event that
  `EditorContext.Refresh` subscribes to.
- `DocumentSession` — `New/Open/SaveSource/Bake*/Export/CurrentLevelPath/RememberLastLevel/Report`
  (everything from `EditorContext.cs:669` down), plus `Notified`.
- `OutlineEditing` — path/polygon point insert (`TryInsertPathPointAtCursor`), hole ops
  (`AddPolygonHole`), the point/hole branches of `DeleteSelected`.
- `EditorContext` retains: document swap, draw height/storey navigation, `Refresh`, and delegating
  properties. Mechanical extraction, no behavior change — do it **after** Phase 1 tests exist.

**3.2 Capability interfaces instead of type-string checks.** `EditorContext` alone has 9
`PrimitiveType == "..."` checks; `LevelView`, `CutHoleTool`, `OpeningTool` add more. Adding an 18th
primitive with editable points today means editing the hub. Add optional capabilities on the
primitive (the registry already gives `IPrimitive` from an instance):
- `IPointOutline` — `bool Closed(inst)`, `int MinPoints`, replaces `HasEditablePoints` /
  `OutlineClosed` (`EditorContext.cs:164-172`); `PathSweepPrimitive` (min 2, closed-param) and
  `PolygonFloorPrimitive` (min 3, always closed) implement it.
- `IHoleHost` — polygon floor's hole encode/decode ops (wraps `PolygonHoles`).
- `IOpeningHost` — wall; `LevelView.AddOpeningBodies` (`LevelView.cs:104`) and `OpeningTool` check
  `prim is IOpeningHost` instead of `== "wall"`.
Route existing static helpers (`PathPoints`, `PolygonHoles`) through these; delete the string checks.

**3.3 Kill the copy-paste helpers.** `GetF/GetI/GetB` + `Begin()/Commit()` are duplicated in **23
files** (every primitive + gizmos + view). Create:
- `Source/Core/Primitives/ParamAccess.cs` — extension methods on `PrimitiveInstanceData`:
  `GetFloat(key, def)`, `GetInt`, `GetBool` (use `TryGetValue` — the current
  `ContainsKey`+indexer double-lookup pattern goes away too).
- `Source/Core/Geometry/SurfaceTools.cs` — `static SurfaceTool BeginTriangles()`,
  `static void CommitWithTangents(SurfaceTool, ArrayMesh)`.
Then delete all 23 private copies. Pure mechanical; Phase 1.2's mesh-sanity tests confirm nothing moved.

**3.4 Data-driven tool registration.** `ToolManager.Setup` hand-maintains four parallel structures
(hotkey dict, id dict, reverse dict, a 700-char help print) — and 20 of 26 letter keys are taken.
Replace with one table: `record ToolDef(string Id, Key Hotkey, string Label, Func<ITool> Make)`;
build all maps + the help text (and feed `HelpOverlay` from the same table so help can't drift).
Consider Godot `InputMap` actions for remapping later; not required now.

**3.5 (Optional, only if UI work continues) split `InspectorPanel` (480 lines)** into the identity
header, the ParamSpec rows builder, and the texture-props block. Low priority — it's gated correctly
and works.

## Phase 4 — Robustness

**4.1 Cap the undo stack.** Unbounded today (`CommandStack`); commands snapshot whole point arrays,
so long sessions grow forever. Swap `Stack<ICommand>` for a deque (e.g. `LinkedList`) capped at ~256,
dropping oldest. Semantics: if the save-marker command gets dropped, `IsDirty` must still report
dirty until the next save — the reference-equality check handles this naturally (marker never found
on top again), but add a Tier-A test for it (extends 1.2.6).

**4.2 Schema migration skeleton.** `SchemaVersion` exists (`LevelDocument.cs:15`) but nothing reads
it. Add `LevelMigrations.Apply(doc)` called from `LevelSerializer.Load`: compare version, run ordered
`Func<LevelDocument, LevelDocument>` steps, warn on future versions. Ship it with a real migration
when the first breaking change lands (roadmap M7 wants the framework exercised, not built on spec).

**4.3 Harden `ResourceIo`.** Fixed temp names (`user://__lb_tmp_save.*`) collide if two builder
instances run; temps are never deleted. Use a GUID suffix, `try/finally` delete, and propagate the
copy error distinctly from the save error. Keep the staging dance itself (invariant #5).

**4.4 Guard the positional slot convention.** In `MaterialResolver.AssignSurfaceMaterials` and
`SceneBaker.BakeMerged`, surfaces are matched to slots by index with conditional trailing surfaces.
One mid-list conditional surface would silently shift every following material. Add a debug
assertion helper: after `BuildMesh`, `GetSurfaceCount()` must equal the count of slots the primitive
actually committed — cheapest form: `Debug.Assert(mesh.GetSurfaceCount() <= prim.MaterialSlots.Count)`
plus a comment-anchored convention note in `IPrimitive`; better form: `BuildMesh` returns surface
names alongside the mesh (bigger API change — only if a real bug appears; the Phase 1.2.2 test
already asserts the ≤ relationship for defaults).

**4.5 Small fixes.**
- `SceneTreePanel`/`InspectorPanel` unsubscribe in `_ExitTree`, good — audit the remaining
  subscribers (`StatusBar`, `HeightIndicatorPanel`, `ProjectPanel`, `Main.updateTitle`) for symmetry.
- `EditorContext.FileStem` only replaces spaces — sanitize the full filename (invalid path chars)
  with the same class of guard as `SceneBaker.SanitizeName`.
- `InstancePicker` serves the *previous physics frame's* pick; a fast click-move can select the
  wrong object. If it's ever reported: do a fresh synchronous raycast inside `Pick()` when called
  from a click (space state is valid in `_UnhandledInput`? — it is not during GUI callbacks; verify,
  else keep cached behavior and document).

## Phase 5 — Features (the roadmap's "Next up", now unblocked)

In the project's own priority order (`docs/ROADMAP.md`):
1. **Per-slot material assignment** — inspector gains a per-slot texture drop row (slot names from
   `IPrimitive.MaterialSlots`); reuses `AssignMaterialCommand` with a single-slot map.
2. **Set-for-type defaults** — a texture drop variant that (a) repaints all instances of a type
   (MacroCommand of AssignMaterial) and (b) persists a per-type default in `LevelDocument`
   (new `[Export] Dictionary TypeDefaults`), superseding `DefaultMaterials.SlotsFor` for new draws.
   Round-trips through Phase 1.2.1's test automatically.
3. **Export-override round-trip verification** (M5's last unchecked box) — becomes a Tier-B test:
   bake textured object → load `.tscn` → assert material on surface, `surface_material_override` null.
4. **Multi-storey polish + instance duplicate (Ctrl+D)** — duplicate = clone data with fresh `Ids.New()`
   through `AddInstanceCommand`; cheap and high-value for level building.
5. Undo-history UI / command names in status bar — `CommandStack` already has names; trivial win.

---

## Suggested execution order for a future session

| Step | Scope | Why first |
|------|-------|-----------|
| 1 | Phase 0 | 30 minutes, unblocks clean commits |
| 2 | Phase 1.1 + 1.2 (tests 1, 2, 5, 6) | Safety net before touching anything |
| 3 | Phase 2.1–2.3 | Biggest user-visible win; tests catch regressions |
| 4 | Phase 3.3 (param/surface helpers) | Mechanical, shrinks every later diff |
| 5 | Phase 3.1–3.2 | The refactor that keeps primitive #18 cheap |
| 6 | Phase 4 | Hardening, small independent items |
| 7 | Phase 5 | Features on a now-solid base |

Verify each step in the running app (F5 by the user) before moving on — this project's history
(see `docs/ROADMAP.md` status notes) shows in-engine eyeballing catches what builds don't.
