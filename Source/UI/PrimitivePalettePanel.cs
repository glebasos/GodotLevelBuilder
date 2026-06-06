using System.Collections.Generic;
using System.Linq;
using Godot;
using LevelBuilder.Core.Primitives;
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

    // Openings aren't registry primitives (they attach to walls via OpeningTool) — list them here.
    private static readonly Entry[] OpeningEntries =
    {
        new("door", "Door", "Openings"),
        new("window", "Window", "Openings"),
    };

    // Lower sorts first; unknown categories fall to the end but keep a stable alphabetical order.
    private static readonly Dictionary<string, int> CategoryOrder = new()
    {
        { "Structure", 0 }, { "Openings", 1 }, { "Vertical", 2 },
    };

    private ToolManager _tools;
    private readonly ButtonGroup _group = new();
    private readonly Dictionary<string, Button> _buttonsById = new();
    private bool _suppressSignal;

    public void Setup(PrimitiveRegistry registry, ToolManager tools)
    {
        _tools = tools;

        AddThemeConstantOverride("margin_left", 8);
        AddThemeConstantOverride("margin_top", 8);
        AddThemeConstantOverride("margin_right", 8);
        AddThemeConstantOverride("margin_bottom", 8);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, // let the flow wrap, scroll vertically only
        };
        AddChild(scroll);

        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(rows);

        IEnumerable<Entry> entries = registry.All
            .Select(p => new Entry(p.TypeId, p.DisplayName, p.Category))
            .Concat(OpeningEntries);

        foreach (IGrouping<string, Entry> category in entries
                     .GroupBy(e => e.Category)
                     .OrderBy(g => CategoryOrder.GetValueOrDefault(g.Key, int.MaxValue))
                     .ThenBy(g => g.Key))
        {
            rows.AddChild(new Label { Text = category.Key, Modulate = new Color(1, 1, 1, 0.6f) });

            var flow = new HFlowContainer();
            rows.AddChild(flow);

            foreach (Entry e in category.OrderBy(e => e.Label))
                flow.AddChild(MakeButton(e));
        }

        _tools.ActiveToolIdChanged += OnActiveToolIdChanged;
    }

    public override void _ExitTree()
    {
        if (_tools != null) _tools.ActiveToolIdChanged -= OnActiveToolIdChanged;
    }

    private Button MakeButton(Entry e)
    {
        var button = new Button
        {
            Text = e.Label,
            ToggleMode = true,
            ButtonGroup = _group,
            FocusMode = FocusModeEnum.None, // don't let a pressed button eat tool hotkeys
            CustomMinimumSize = new Vector2(96, 36),
        };
        string id = e.Id;
        button.Pressed += () => { if (!_suppressSignal) _tools.ActivateToolById(id); };
        _buttonsById[id] = button;
        return button;
    }

    /// <summary>Reflect the tool manager's active tool without re-triggering activation.</summary>
    private void OnActiveToolIdChanged(string id)
    {
        _suppressSignal = true;
        if (id != null && _buttonsById.TryGetValue(id, out Button button))
            button.SetPressedNoSignal(true);
        else
            foreach (Button b in _buttonsById.Values) b.SetPressedNoSignal(false); // Select tool
        _suppressSignal = false;
    }
}
