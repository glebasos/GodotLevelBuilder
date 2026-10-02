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
| Moving Platform | `M` | `platform` | yaw, width/depth/thickness, moveX/Y/Z (local), period, spin °/s, phase | `Marker3D` + `Mesh` child; `scb_move` (Vector3), `scb_period`, `scb_spin`, `scb_phase` |
| Bumper | `V` | `bumper` | yaw, radius, height, sides, kick m/s | `Marker3D` + `Mesh` child; `scb_strength` |
| Conveyor | `Q` | `conveyor` | yaw, width, length, speed m/s | `Marker3D`; `scb_size` (Vector2 w, length), `scb_speed` |
| Trigger Zone | `H` | `trigger` | yaw, width/depth/height, channel 1-99, once | `Marker3D`; `scb_size` (Vector3 w,h,d), `scb_channel`, `scb_once` (0/1) |

### Motion on any piece (moving walls / doors)

Every non-marker primitive has a **Motion** section in the inspector (`Core/Primitives/Motion.cs`, stored
as `m_*` params): mode None / Loop / On trigger, slide X/Y/Z (instance-local), rotate axis + angle about a
hinge point (instance-local), spin °/s (loop), period/travel time, phase, trigger channel, return-after.
The editor draws a cyan ghost at the end pose and a pink cube on the hinge. A moving piece is **not**
merged into the static chunk: it bakes as `Marker3D` kind `mover` at its placed pose with its textured
mesh as a `Mesh` child and `scb_mode/move/axis/angle/hinge/spin/period/phase/channel/return`. The game's
`Mover.cs` mirrors `Motion.PoseAt` — change both together. Trigger zones fire channels on `BuilderLevel`
(`ChannelFired`, `FiredChannels`) — also the planned hook for a narrator reacting to the player's route.

Platform and bumper are `GeometryMarkerPrimitive`s: they borrow Floor / Cylinder geometry (textured, with
material slots) and bake it as a `Mesh` child of their marker (embedded textures on export). Their lowest
point at both ends of a platform's travel counts toward `scb_fall_out_y`. The game fits colliders to the
mesh AABB (box / cylinder) — keep platform/bumper meshes box/cylinder-shaped.

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
- `Models/Objects/{MovingPlatform,Bumper,Conveyor}.cs` — built in code by `BuilderLevel` from those markers
  (no scenes); `BuilderMeshes.AdoptMesh` moves the baked mesh under the body. Platforms (StaticBody3D, ball carried positionally via `CarryDelta`) move via their
  LOCAL transform so they tilt with the stage. `LevelController` shows a "FALL OUT!" banner while falling.
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

- **Player-made levels** — first cut done: Project tab → **Export Player Level (.scblevel)** writes a zip
  (`level.json` markers/settings + `level.glb` meshes, textures embedded) to `<workspace>/shared/`, copied
  to `<target>/levels/` too. Game: `PlayerLevelLoader` validates + rebuilds the chunk; never loads a Godot
  Resource from the file. Builder: `Core/Build/PlayerLevelExporter.cs`. Still to do: an in-game custom-level
  browser (set `BuilderLevel.PendingLevelFile`, change to the template), level thumbnails, a format
  version bump policy. JSON marker `transform` = 12 floats (basis columns X,Y,Z then origin).
- ~~Rotate gizmo for marker yaw~~ — done: pink `YawHandle` on every marker, 15° snap.
- More pieces: goal variants, switches, wind/fans, ice/sticky floors.
