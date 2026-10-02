using System.Collections.Generic;
using Godot;
using LevelBuilder.Editor.Session;

namespace LevelBuilder.Editor.Tools;

/// <summary>
/// Routes input to the active tool and switches tools by hotkey.
///   S → Select   F → Floor   W → Wall   Esc/right-click → cancel
///   left-click → tool action   Delete → delete selected
///   Ctrl+Z / Ctrl+Y → undo / redo   Ctrl+B → bake .tscn   Ctrl+S → save .tres
/// </summary>
public partial class ToolManager : Node
{
    private EditorContext _ctx;
    private ITool _active;
    private Dictionary<Key, ITool> _tools;

    /// <summary>Palette id -> the tool it activates. Covers draw-primitive tools AND openings (door/window).</summary>
    private Dictionary<string, ITool> _toolsById;
    private Dictionary<ITool, string> _idByTool;
    private Dictionary<string, string> _hotkeyById;

    /// <summary>
    /// Fires when the active tool changes, carrying the palette id of the tool (a primitive TypeId,
    /// "door"/"window", "cut_hole" or "select"). The palette and status bar sync from this.
    /// </summary>
    public event System.Action<string> ActiveToolIdChanged;

    public void Setup(EditorContext ctx)
    {
        _ctx = ctx;

        var select = new SelectTool();
        var floor = new FloorDrawTool();
        var polygonFloor = new PolygonFloorDrawTool();
        var circlePlane = new CirclePlaneDrawTool();
        var halfCircle = new HalfCircleDrawTool();
        var cutHole = new CutHoleTool();
        var wall = new WallDrawTool();
        var ramp = new RampDrawTool();
        var rampPlane = new RampPlaneDrawTool();
        var bankedCurve = new BankedCurveDrawTool();
        var halfPipe = new HalfPipeDrawTool();
        var edgeCurb = new EdgeCurbDrawTool();
        var cylinder = new CylinderDrawTool();
        var curvedWall = new CurvedWallDrawTool();
        var dome = new DomeDrawTool();
        var pathSweep = new PathSweepDrawTool();
        var door = new OpeningTool(OpeningPreset.Door);
        var window = new OpeningTool(OpeningPreset.Window);

        _tools = new Dictionary<Key, ITool>
        {
            { Key.S, select },
            { Key.F, floor },
            { Key.Y, polygonFloor },
            { Key.I, circlePlane },
            { Key.J, halfCircle },
            { Key.K, cutHole },
            { Key.W, wall },
            { Key.D, door },
            { Key.N, window },
            { Key.R, ramp },
            { Key.G, rampPlane },
            { Key.C, bankedCurve },
            { Key.U, halfPipe },
            { Key.E, edgeCurb },
            { Key.L, cylinder },
            { Key.A, curvedWall },
            { Key.O, dome },
            { Key.P, pathSweep },
        };

        _toolsById = new Dictionary<string, ITool>
        {
            { "select", select },
            { "floor", floor },
            { "polygon_floor", polygonFloor },
            { "circle_plane", circlePlane },
            { "half_circle", halfCircle },
            { "cut_hole", cutHole },
            { "wall", wall },
            { "ramp", ramp },
            { "ramp_plane", rampPlane },
            { "banked_curve", bankedCurve },
            { "half_pipe", halfPipe },
            { "edge_curb", edgeCurb },
            { "cylinder", cylinder },
            { "curved_wall", curvedWall },
            { "dome", dome },
            { "path_sweep", pathSweep },
            { "door", door },
            { "window", window },
        };
        _idByTool = new Dictionary<ITool, string>();
        foreach (var (id, tool) in _toolsById) _idByTool[tool] = id;

        _hotkeyById = new Dictionary<string, string>();
        foreach (var (key, tool) in _tools)
            if (_idByTool.TryGetValue(tool, out string id))
                _hotkeyById[id] = key.ToString();

        // Start in Select so the very first click in the viewport does something.
        SetActive(select);
    }

    /// <summary>Cancels any in-progress tool operation (e.g. a half-drawn primitive) before a
    /// document swap, so a dangling draw can't reference the old document.</summary>
    public void CancelActive() => _active?.OnCancel();

    /// <summary>Hotkey letter for a palette tool id, or null — used for palette tooltips/help.</summary>
    public string HotkeyFor(string id) => _hotkeyById?.GetValueOrDefault(id);

    /// <summary>Palette id of the active tool (see <see cref="ActiveToolIdChanged"/>), or null before Setup.</summary>
    public string ActiveToolId => _active != null ? _idByTool.GetValueOrDefault(_active) : null;

    /// <summary>Display name of the active tool (e.g. "Polygon Floor").</summary>
    public string ActiveToolName => _active?.Name ?? "";

    /// <summary>One-line how-to for a tool id, shown in the status bar while that tool is active.</summary>
    public static string HintFor(string id) => id != null ? Hints.GetValueOrDefault(id) : null;

    private static readonly Dictionary<string, string> Hints = new()
    {
        // No "select" entry on purpose: Select is the resting tool, so the status bar falls back to the
        // general camera/controls hint (orbit, pan, zoom, frame, F1) — its only on-screen home.
        { "floor", "Click two cells to span a rectangular floor · Esc/RMB cancel" },
        { "polygon_floor", "Click corners · click the first corner again to close · Esc/RMB cancel" },
        { "circle_plane", "Click the centre, then a point on the rim (sets the radius)" },
        { "half_circle", "Click the diameter centre, then a point on the arc (sets radius + bulge direction)" },
        { "cut_hole", "With a polygon floor selected: click hole corners · click the first corner to close" },
        { "wall", "Click corners to chain walls · Esc/RMB to stop the chain" },
        { "door", "Click a wall to place a door" },
        { "window", "Click a wall to place a window" },
        { "ramp", "Click the bottom end, then the top end" },
        { "ramp_plane", "Click the bottom end, then the top end" },
        { "banked_curve", "Click the entry corner, then the heading (distance = radius); curves left" },
        { "half_pipe", "Click the entry, then the heading (distance = length)" },
        { "edge_curb", "Click two cells to frame a rectangle with a curb" },
        { "cylinder", "Click the centre, then a point on the rim (sets the radius)" },
        { "curved_wall", "Click the entry corner, then the heading (distance = radius); curves left" },
        { "dome", "Click the centre, then a point on the rim (sets the radius)" },
        { "path_sweep", "Click points · click the last point again to finish, or the first to close a loop" },
    };

    /// <summary>Activate a tool by its palette id (palette click). No-op if unknown.</summary>
    public void ActivateToolById(string id)
    {
        if (_toolsById != null && _toolsById.TryGetValue(id, out ITool tool))
            SetActive(tool);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey k when k.Pressed && !k.Echo:
                HandleKey(k);
                break;
            case InputEventMouseButton mb when mb.Pressed && mb.ButtonIndex == MouseButton.Left:
                // Clicking the 3D view releases any focused panel control (SpinBox/LineEdit),
                // otherwise its focus keeps eating tool hotkeys. Panels live in the MAIN window's
                // GUI, not this SubViewport, so query the root viewport's focus owner.
                GetTree().Root.GuiGetFocusOwner()?.ReleaseFocus();
                _active?.OnClick();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton mb when !mb.Pressed && mb.ButtonIndex == MouseButton.Left:
                _active?.OnRelease(); // not marked handled — LMB-release was never consumed before
                break;
            case InputEventMouseButton mb when mb.Pressed && mb.ButtonIndex == MouseButton.Right:
                _active?.OnCancel();
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_active == null) return;
        _ctx.Cursor.Mode = _active.SnapMode; // keep the cursor in the tool's mode (neutralizes Tab mid-draw)
        _active.UpdatePreview();
    }

    private void HandleKey(InputEventKey k)
    {
        if (k.CtrlPressed)
        {
            if (k.Keycode == Key.Z && k.ShiftPressed) { _ctx.Redo(); return; } // Ctrl+Shift+Z — common redo alias
            if (k.Keycode == Key.Z) { _ctx.Undo(); return; }
            if (k.Keycode == Key.Y) { _ctx.Redo(); return; }
            if (k.Keycode == Key.B) { _ctx.BakeToGodot(); return; }
            if (k.Keycode == Key.S) { _ctx.SaveSource(); return; }
        }

        if (k.Keycode == Key.Escape) { _active?.OnCancel(); return; }
        if (k.Keycode == Key.Delete) { _ctx.DeleteSelected(); return; }

        // Storey navigation: + up / − down (main-row "+" is Shift+Equal → still reports as Equal).
        // Cancel any in-progress draw first so a half-placed primitive can't straddle two elevations.
        if (k.Keycode is Key.Equal or Key.KpAdd) { _active?.OnCancel(); _ctx.StoreyUp(); GetViewport().SetInputAsHandled(); return; }
        if (k.Keycode is Key.Minus or Key.KpSubtract) { _active?.OnCancel(); _ctx.StoreyDown(); GetViewport().SetInputAsHandled(); return; }

        if (_tools.TryGetValue(k.Keycode, out ITool tool))
        {
            SetActive(tool);
            GetViewport().SetInputAsHandled();
        }
    }

    private void SetActive(ITool tool)
    {
        _active?.Deactivate();
        // Most tools start from a clean slate; a selection-preserving tool (cut-hole) operates ON the
        // current selection, so keep it.
        if (tool is not IPreservesSelection) _ctx.ClearSelection();
        _active = tool;
        _ctx.Cursor.Enabled = tool.UsesGridCursor;
        tool.Activate(_ctx);
        GD.Print($"[tool] {tool.Name} active");
        ActiveToolIdChanged?.Invoke(_idByTool.GetValueOrDefault(tool));
    }
}
