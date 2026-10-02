using System.Collections.Generic;
using System.Linq;
using Godot;
using LevelBuilder.Core.Primitives;
using LevelBuilder.Editor.Session;
using LevelBuilder.Editor.Tools;

namespace LevelBuilder.UI;

/// <summary>
/// Primitive palette — the "Primitives" tab of the bottom dock. Lists the registered primitives
/// plus the wall openings (door/window), grouped by category, as toggle buttons; clicking one
/// activates that tool exactly as its keyboard hotkey would. A ButtonGroup keeps the buttons
/// mutually exclusive, and we mirror the tool manager's active tool back into the pressed state so
/// switching tools by hotkey keeps the palette in sync.
///
/// FocusMode is None on the buttons so they can't swallow tool hotkeys once clicked (same reason
/// the scene tree disables focus).
/// </summary>
public partial class PrimitivePalettePanel : MarginContainer
{
    /// <summary>A clickable palette item: a tool id (see <see cref="ToolManager"/>) + how to label/group it.</summary>
    private readonly record struct Entry(string Id, string Label, string Category);

    // Non-primitive tools: Select + cut-hole (edit tools), and the openings (which attach to walls via
    // OpeningTool rather than being registry primitives). Without these, Select and Cut Hole were only
    // reachable by hotkey.
    private static readonly Entry[] ExtraEntries =
    {
        new("select", "Select", "Tools"),
        new("cut_hole", "Cut Hole", "Tools"),
        new("door", "Door", "Openings"),
        new("window", "Window", "Openings"),
    };

    // Lower sorts first; unknown categories fall to the end but keep a stable alphabetical order.
    private static readonly Dictionary<string, int> CategoryOrder = new()
    {
        { "Tools", -1 }, { "Structure", 0 }, { "Openings", 1 }, { "Vertical", 2 }, { "Curves", 3 },
    };

    private ToolManager _tools;
    private readonly ButtonGroup _group = new();
    private readonly Dictionary<string, Button> _buttonsById = new();
    private bool _suppressSignal;

    public void Setup(PrimitiveRegistry registry, ToolManager tools, EditorContext ctx)
    {
        _tools = tools;

        UiFactory.ApplyMargin(this);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, // let the flow wrap, scroll vertically only
        };
        AddChild(scroll);

        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(rows);

        rows.AddChild(MakeWidthAnchorRow(ctx));

        IEnumerable<Entry> entries = registry.All
            .Select(p => new Entry(p.TypeId, p.DisplayName, p.Category))
            .Concat(ExtraEntries);

        foreach (IGrouping<string, Entry> category in entries
                     .GroupBy(e => e.Category)
                     .OrderBy(g => CategoryOrder.GetValueOrDefault(g.Key, int.MaxValue))
                     .ThenBy(g => g.Key))
        {
            rows.AddChild(UiFactory.Section(category.Key));

            var flow = new HFlowContainer();
            rows.AddChild(flow);

            // "Tools" keeps its declared order (Select first); primitive groups sort alphabetically.
            foreach (Entry e in category.Key == "Tools" ? category.AsEnumerable() : category.OrderBy(e => e.Label))
                flow.AddChild(MakeButton(e));
        }

        _tools.ActiveToolIdChanged += OnActiveToolIdChanged;
        OnActiveToolIdChanged(_tools.ActiveToolId); // the initial tool (Select) was activated before we subscribed
    }

    public override void _ExitTree()
    {
        if (_tools != null) _tools.ActiveToolIdChanged -= OnActiveToolIdChanged;
    }

    /// <summary>
    /// "Draw from" dropdown controlling how the width-based tools (ramp, ramp plane)
    /// anchor their fixed width to the two-click line: Edge (default — the line is the near edge, sits on the
    /// adjacent tiles) or Center (the line is the centreline, so mirror-image draws come out symmetric).
    /// Session-only; writes straight to <see cref="EditorContext.WidthFromCenter"/>.
    /// </summary>
    private HBoxContainer MakeWidthAnchorRow(EditorContext ctx)
    {
        var row = new HBoxContainer();

        var label = new Label
        {
            Text = "Draw width from:",
            TooltipText = "How ramps anchor their width to the drawn line.",
            MouseFilter = MouseFilterEnum.Stop,
        };
        row.AddChild(label);

        var option = new OptionButton
        {
            FocusMode = FocusModeEnum.None, // don't swallow tool hotkeys once clicked
            TooltipText = "Edge: the line is the strip's near edge (sits on adjacent tiles).\n"
                + "Center: the line is the centreline (symmetric mirror-image draws).",
        };
        option.AddItem("Edge", 0);
        option.AddItem("Center", 1);
        option.Select(ctx.WidthFromCenter ? 1 : 0);
        option.ItemSelected += index => ctx.WidthFromCenter = index == 1;
        row.AddChild(option);

        return row;
    }

    private Button MakeButton(Entry e)
    {
        string hotkey = _tools.HotkeyFor(e.Id);
        var button = new Button
        {
            Text = hotkey != null ? $"{e.Label}  ({hotkey})" : e.Label, // hotkey on the button teaches the shortcut
            ToggleMode = true,
            ButtonGroup = _group,
            FocusMode = FocusModeEnum.None, // don't let a pressed button eat tool hotkeys
            CustomMinimumSize = UiConstants.ButtonMin,
            TooltipText = ToolManager.HintFor(e.Id) ?? e.Label,
        };
        string id = e.Id;
        button.Pressed += () => { if (!_suppressSignal) _tools.ActivateToolById(id); };
        _buttonsById[id] = button;
        return button;
    }

    /// <summary>Reflect the tool manager's active tool without re-triggering activation.
    /// Unpress everything first: SetPressedNoSignal bypasses the ButtonGroup's exclusivity
    /// (only real clicks enforce it), so a hotkey switch would otherwise leave the previous
    /// tool's button stuck highlighted.</summary>
    private void OnActiveToolIdChanged(string id)
    {
        _suppressSignal = true;
        foreach (Button b in _buttonsById.Values) b.SetPressedNoSignal(false);
        if (id != null && _buttonsById.TryGetValue(id, out Button button))
            button.SetPressedNoSignal(true);
        _suppressSignal = false;
    }
}
