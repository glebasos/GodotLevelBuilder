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

    /// <summary>
    /// Fires when the active tool changes, carrying the palette id of the tool (a primitive TypeId,
    /// or "door"/"window"), or null for tools with no palette entry (Select). The palette syncs its
    /// highlight from this.
    /// </summary>
    public event System.Action<string> ActiveToolIdChanged;

    public void Setup(EditorContext ctx)
    {
        _ctx = ctx;

        var floor = new FloorDrawTool();
        var wall = new WallDrawTool();
        var ramp = new RampDrawTool();
        var stairs = new StairsDrawTool();
        var rampPlane = new RampPlaneDrawTool();
        var stairPlane = new StairPlaneDrawTool();
        var door = new OpeningTool(OpeningPreset.Door);
        var window = new OpeningTool(OpeningPreset.Window);

        _tools = new Dictionary<Key, ITool>
        {
            { Key.S, new SelectTool() },
            { Key.F, floor },
            { Key.W, wall },
            { Key.D, door },
            { Key.N, window },
            { Key.R, ramp },
            { Key.T, stairs },
            { Key.G, rampPlane },
            { Key.H, stairPlane },
        };

        _toolsById = new Dictionary<string, ITool>
        {
            { "floor", floor },
            { "wall", wall },
            { "ramp", ramp },
            { "stairs", stairs },
            { "ramp_plane", rampPlane },
            { "stair_plane", stairPlane },
            { "door", door },
            { "window", window },
        };
        _idByTool = new Dictionary<ITool, string>();
        foreach (var (id, tool) in _toolsById) _idByTool[tool] = id;

        GD.Print("[tools] S = Select (click door/window to select, drag to move it along the wall), F = Floor, W = Wall, R = Ramp, T = sTairs, G = ramp plane (Gradient), H = stair plane, D = Door, N = wiNdow, +/- = storey up/down, Del = delete, Esc/RMB = cancel, Ctrl+Z/Y = undo/redo, Ctrl+B = bake, Ctrl+S = save");
    }

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
        _ctx.ClearSelection();
        _active = tool;
        _ctx.Cursor.Enabled = tool.UsesGridCursor;
        tool.Activate(_ctx);
        GD.Print($"[tool] {tool.Name} active");
        ActiveToolIdChanged?.Invoke(_idByTool.GetValueOrDefault(tool));
    }
}
