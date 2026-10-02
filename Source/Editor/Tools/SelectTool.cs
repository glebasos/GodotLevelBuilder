using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Editor.Gizmos;
using LevelBuilder.Editor.Grid;
using LevelBuilder.Editor.Session;

namespace LevelBuilder.Editor.Tools;

/// <summary>
/// Click a placed primitive to select it (highlighted) and <b>drag its body</b> to move it across
/// the floor. Drag a resize handle to grow/shrink a dimension. Click a door/window to select the
/// opening (solid placeholder) and drag it along its wall. Every drag runs through one
/// <see cref="IEditHandle"/>: a press that doesn't move is a plain select, a real drag commits one
/// undoable edit on release. Clicking empty space clears the selection. Delete removes the selected
/// instance or opening (handled by ToolManager). No grid cursor.
/// </summary>
public sealed class SelectTool : ITool
{
    private EditorContext _ctx;

    private IEditHandle _active; // the handle currently being dragged, if any
    private Vector3 _grabStart;
    private Vector2 _grabScreen; // mouse pixel position when the handle was grabbed
    private bool _dragging;      // becomes true once the mouse moves past the click deadzone

    /// <summary>Pixels the mouse must travel before a press is treated as a drag (not a select-click).</summary>
    private const float DragDeadzonePx = 6f;

    public string Name => "Select";
    public GridSnapMode SnapMode => GridSnapMode.Cell; // unused (cursor hidden)
    public bool UsesGridCursor => false;

    public void Activate(EditorContext ctx) => _ctx = ctx;
    public void Deactivate() => CancelDrag(); // switching tools mid-drag must not leave an uncommitted edit

    public void OnClick()
    {
        PickResult r = _ctx.Picker.Pick();

        if (r.IsHandle)
        {
            if (r.HandleIndex < _ctx.Handles.Count)
            {
                IEditHandle h = _ctx.Handles[r.HandleIndex];
                // A path-point marker selects that point (view state) rather than starting a drag.
                if (h is IPathPointSelect ps) { _ctx.SelectRingPoint(ps.Ring, ps.Index); return; }
                Begin(h); // Resize/move: grab the gizmo handle without changing the selection.
            }
            return;
        }

        bool ctrl = Input.IsKeyPressed(Key.Ctrl);

        // Path3D-style add-point: a plain click near the selected path's line inserts a point there. Runs
        // before the body/empty handling (and works over the swept mesh) so the curve line takes priority;
        // it's a cheap no-op false unless a single path_sweep is selected and the click is on the line.
        if (!ctrl && _ctx.TryInsertPathPointAtCursor()) return;

        if (!r.Hit) { if (!ctrl) _ctx.ClearSelection(); return; } // Ctrl+click on empty keeps the current set

        // A piece inside a doorway (door panel) shadows the opening around it: clicking the already-selected
        // panel again cycles to the opening (and the opening's next click back to the panel, via the
        // normal path below, since the panel wins the pick).
        if (!ctrl && r.HasAltOpening && _ctx.SelectedOpeningId == null
            && _ctx.SelectedIds.Count == 1 && _ctx.IsSelected(r.InstanceId))
            r = new PickResult(r.AltWallId, r.Position, r.AltOpeningId);

        if (r.IsOpening)
        {
            // Openings are single-select; Ctrl is ignored (multi-select is instances-only).
            _ctx.SelectOpening(r.InstanceId, r.OpeningId);
            PrimitiveInstanceData wall = _ctx.GetInstance(r.InstanceId);
            OpeningData opening = FindOpening(wall, r.OpeningId);
            if (opening != null) Begin(new OpeningMoveHandle(wall, opening, _ctx.SelectedInstanceOffset));
            return;
        }

        if (ctrl)
        {
            // Toggle this instance in/out of the multi-selection. No drag begins on a Ctrl-click.
            _ctx.ToggleSelect(r.InstanceId);
            return;
        }

        // Plain click on an instance already in a multi-selection: keep the set, drag the whole group.
        if (_ctx.SelectedIds.Count > 1 && _ctx.IsSelected(r.InstanceId))
        {
            var instances = _ctx.SelectedInstances();
            if (instances.Count > 0)
                Begin(new MultiMoveHandle(instances, _ctx.Document.Grid.CellSize, _ctx.SelectedInstanceOffset.Y));
            return;
        }

        // Otherwise collapse to a single selection and move just it.
        _ctx.Select(r.InstanceId);
        PrimitiveInstanceData inst = _ctx.GetInstance(r.InstanceId);
        if (inst != null) Begin(new InstanceMoveHandle(inst, _ctx.Document.Grid.CellSize, _ctx.SelectedInstanceOffset.Y));
    }

    public void UpdatePreview()
    {
        if (_active == null) return;

        // The release that ends a drag is delivered as an _UnhandledInput event to the viewport — but
        // if the mouse is let go over a UI panel (inspector, scene tree, the texture dock), that panel
        // consumes the release and OnRelease never fires. Without this guard the handle stays armed and
        // the object follows the cursor forever. Poll the real button state and disarm as soon as it's up.
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) { OnRelease(); return; }

        // Click-vs-drag deadzone: a plain select-click shouldn't move/resize anything. Only start
        // applying once the mouse has travelled past a few pixels (jitter on a narrow viewport can
        // otherwise project to a whole-cell snap and make the object jump on selection).
        if (!_dragging && _ctx.Picker.MouseScreen().DistanceTo(_grabScreen) < DragDeadzonePx) return;
        _dragging = true;

        if (!_ctx.Picker.MouseRay(out Vector3 from, out Vector3 dir)) return;
        if (!_active.Grab(from, dir, out Vector3 now)) return;

        _active.Preview(_grabStart, now);
        // Full refresh so the handle widgets track the resize live. Safe: _active is held here (not
        // re-looked-up from Ctx.Handles), and a resize face moves along its own axis, so rebuilding
        // the widgets doesn't disturb the dragged handle's projection line or captured originals.
        _ctx.Refresh();
    }

    public void OnRelease()
    {
        if (_active == null) return;

        IEditHandle handle = _active;
        _active = null;
        // Build the command before Execute: Execute runs Refresh (which frees the handle widgets).
        if (handle.Changed) _ctx.Commands.Execute(handle.Commit(_ctx.Refresh));
    }

    public void OnCancel()
    {
        if (_active != null) { CancelDrag(); return; }
        _ctx.ClearSelection();
    }

    private void Begin(IEditHandle handle)
    {
        if (!_ctx.Picker.MouseRay(out Vector3 from, out Vector3 dir)) { _active = null; return; }
        if (!handle.Grab(from, dir, out _grabStart)) { _active = null; return; }
        _active = handle;
        _grabScreen = _ctx.Picker.MouseScreen();
        _dragging = false;
    }

    private void CancelDrag()
    {
        if (_active == null) return;
        _active.Cancel();
        _active = null;
        _ctx.Refresh(); // resync the view + gizmos to the rolled-back state
    }

    private static OpeningData FindOpening(PrimitiveInstanceData wall, string id)
    {
        if (wall == null) return null;
        foreach (OpeningData o in wall.Openings)
            if (o.Id == id) return o;
        return null;
    }
}
