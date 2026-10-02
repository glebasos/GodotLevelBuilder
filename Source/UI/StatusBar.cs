using Godot;
using LevelBuilder.Editor.Session;
using LevelBuilder.Editor.Tools;

namespace LevelBuilder.UI;

/// <summary>
/// Bottom status strip: active tool, draw height, selection count, and a right-aligned controls
/// hint. Subscribes to <see cref="EditorContext.Changed"/> (fired per drag frame — the work here is
/// just label text, cheap enough) and <see cref="ToolManager.ActiveToolIdChanged"/>.
/// </summary>
public partial class StatusBar : PanelContainer
{
    private EditorContext _ctx;
    private ToolManager _tools;
    private Label _tool;
    private Label _height;
    private Label _selection;
    private Label _hint;

    private const string DefaultHint =
        "LMB draw/select  ·  MMB orbit  ·  Shift+MMB pan  ·  wheel zoom  ·  . frame  ·  7 top-down  ·  F1 help";

    public void Setup(EditorContext ctx, ToolManager tools)
    {
        _ctx = ctx;
        _tools = tools;

        var row = new HBoxContainer();
        AddChild(row);

        _tool = Cell("Tool: Select", "The active tool — switch in the Primitives palette or by hotkey.");
        row.AddChild(_tool);
        row.AddChild(new VSeparator());
        _height = Cell("Height: 0.00 m", "Draw-plane elevation (▲/▼ in the viewport corner, +/- for layers).");
        row.AddChild(_height);
        row.AddChild(new VSeparator());
        _selection = Cell("Nothing selected", "Ctrl+click to multi-select; Del deletes the selection.");
        row.AddChild(_selection);

        // Right side: how to use the active tool (falls back to the general camera/controls hint).
        _hint = new Label
        {
            Text = DefaultHint,
            Modulate = UiConstants.FontDim,
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        row.AddChild(_hint);

        _ctx.Changed += Refresh;
        _tools.ActiveToolIdChanged += OnToolChanged;
        OnToolChanged(_tools.ActiveToolId); // the initial tool was activated before we subscribed
        Refresh();
    }

    public override void _ExitTree()
    {
        if (_ctx != null) _ctx.Changed -= Refresh;
        if (_tools != null) _tools.ActiveToolIdChanged -= OnToolChanged;
    }

    private static Label Cell(string text, string tooltip) => new()
    {
        Text = text,
        TooltipText = tooltip,
        MouseFilter = MouseFilterEnum.Stop, // so the tooltip shows
    };

    private void OnToolChanged(string id)
    {
        string name = _tools.ActiveToolName;
        string hotkey = id != null ? _tools.HotkeyFor(id) : null;
        _tool.Text = hotkey != null ? $"Tool: {name} ({hotkey})" : $"Tool: {name}";
        string hint = ToolManager.HintFor(id);
        _hint.Text = hint ?? DefaultHint;
        _hint.TooltipText = hint != null ? $"{hint}\n\n{DefaultHint}" : "";
        _hint.MouseFilter = MouseFilterEnum.Stop; // so a trimmed hint can still be read in full via tooltip
    }

    private void Refresh()
    {
        _height.Text = $"Height: {_ctx.DrawHeight:0.00} m";
        int n = _ctx.SelectedIds.Count;
        _selection.Text = _ctx.SelectedOpeningId != null ? "Opening selected"
            : n == 0 ? "Nothing selected"
            : n == 1 ? "1 object selected"
            : $"{n} objects selected";
    }
}
