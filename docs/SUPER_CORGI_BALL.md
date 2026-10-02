# Super Corgi Ball integration (branch `super-corgi-ball`)

This branch turns the builder into the level editor for **Super Corgi Ball** (a Super Monkey Ball–style
game; project at `E:\Projects\Godot\SuperCorgiBall`, branch `level-builder-integration`).

## What changed vs. master

- **Removed:** Stairs + Stair Plane (steps don't suit a rolling ball; ramps cover slopes).
- **Kept:** walls, doors/windows (openings), storeys — useful for multi-level stages.
- **Added:** a **Gameplay** palette category with three marker primitives:

| Tool | Key | TypeId | Params | Baked as |
|------|-----|--------|--------|----------|
| Start | `T` | `spawn` | yaw, drop height, time limit, fall-out depth | `Marker3D`, `scb_kind="spawn"`, `scb_drop` |
| Goal  | `X` | `goal`  | yaw | `Marker3D`, `scb_kind="goal"` |
| Bone  | `B` | `bone`  | yaw, height | `Marker3D`, `scb_kind="bone"` (lifted by height) |

Markers are ordinary `PrimitiveInstanceData` (select / move / undo / save all work), implemented as
`MarkerPrimitive` subclasses with no material slots. In the editor they draw a coloured proxy; the
**baker skips them for geometry and collision** and emits them instead.

## Baked contract (what the game reads)

Both bake modes (per-object and merged) append, only if the level has markers:

```
<LevelRoot>
  …geometry / Collision…
  Markers  (Node3D)
    metadata/scb_time_limit = <float s>     # from the Start marker; 0 = game default
    metadata/scb_fall_out_y = <float>       # lowest geometry Y − Start's fall-out depth (level-local)
    Spawn_<id> (Marker3D)  metadata/scb_kind="spawn", scb_drop=<float>
    Goal_<id>  (Marker3D)  metadata/scb_kind="goal"
    Bone_<id>  (Marker3D)  metadata/scb_kind="bone"
```

- Transforms are level-local (storey elevation baked in). Facing = local **−Z** (yaw 0 → −Z).
- Node names follow the usual stable `{Type}_{id}` rule. The game keys off `scb_kind`, not names.
- Exactly one Start is expected; the baker warns otherwise.
- **This contract is shared with the game.** Change keys/meaning only together with
  `SuperCorgiBall/Models/BuilderLevel.cs`.

## Game side

- `Models/BuilderLevel.cs` — sits under the level's `PivotController`. Instances the baked chunk
  (`Level` export, or a chunk dropped in as a child), swaps `goal`/`bone` markers for `FinishGate` /
  `Bone`, and hands spawn + settings to `LevelController` (`ApplyTo`, `PlaceBall`).
- `LevelController` — calls the above in `_Ready`; new `FallOutEnabled` / `FallOutY` /
  `FallOutResetDelay` (ball below the height in pivot-local space → reset after a delay).
- `BallCamera.SnapBehindTarget(yaw)` — starts the camera behind the ball facing the Start arrow.
- `Scenes/Levels/BuilderLevelTemplate.tscn` — generic level (controller, pivot, Stage, PlayerHolder,
  minimap, sky). **New level =** New Inherited Scene from it → set `PivotController/Stage.Level` to the
  exported chunk → add it to `LevelSwitcher.Levels`.
- `Scenes/Levels/BuilderMarkerTest.tscn` + `SlopLevelBuilder/levels/UCS_Lvl1_MarkerTest.tscn` — a
  hand-written fixture (Level1's geometry with markers matching Level1's placements) for testing the
  game side without a fresh bake.

## Workflow

1. Build the stage; place **Start**, **Goal**, **Bones**; set time limit / fall-out on the Start.
2. Project tab → **Export to Game** (merged) → `<target>/levels/<Name>.tscn`. If the level has a Start,
   the export also writes `<Name>_Play.tscn` next to it (once — never overwritten; see
   `Core/Build/PlaySceneWriter.cs`): the game's template with `Stage.Level` = the chunk.
3. In the game: open `<Name>_Play.tscn`, **F6**. Add it to `LevelSwitcher.Levels` when it's a keeper.
   (F6 on the bare chunk shows a grey screen — it's only geometry + markers.)

## Roadmap

- **Player-made levels (post-release):** the standalone builder ships to players, but `.tscn`/`.tres`
  can carry scripts, so shared levels must use a **data-only** format the game parses: JSON (markers +
  settings) + glTF mesh (`GLTFDocument` at runtime), collision rebuilt from the mesh. Verify runtime glTF
  in Godot 4.7 first. Note builder = net10.0, game = net8.0 if Core code is ever shared.
- Rotate gizmo for marker yaw (currently inspector only).
- More gameplay pieces: bumpers, moving/rotating platforms, conveyor, goal variants, "FALL OUT" UI.
