using Godot;

namespace LevelBuilder.UI;

/// <summary>
/// F1 hotkey cheat sheet: a dimmed full-screen overlay with the complete tool/command reference
/// (previously only a one-time console print). Click anywhere, Esc, or F1 again to dismiss. Hidden by
/// default; while hidden it ignores mouse input entirely.
/// </summary>
public partial class HelpOverlay : Control
{
    private static readonly (string Keys, string Action)[] Tools =
    {
        ("S", "Select / move (Ctrl+click multi-select)"),
        ("F", "Floor"),
        ("Y", "Polygon floor"),
        ("I", "Circle plane"),
        ("J", "Half circle"),
        ("K", "Cut hole (in selected polygon floor)"),
        ("W", "Wall"),
        ("D", "Door opening"),
        ("N", "Window opening"),
        ("R", "Ramp"),
        ("G", "Ramp plane (gradient)"),
        ("C", "Banked curve"),
        ("U", "Half-pipe (U-channel)"),
        ("E", "Edge curb"),
        ("L", "Cylinder"),
        ("A", "Curved (arc) wall"),
        ("O", "Dome / bowl"),
        ("P", "Path sweep"),
        ("T", "Start (ball spawn)"),
        ("X", "Goal gate"),
        ("B", "Bone collectible"),
    };

    private static readonly (string Keys, string Action)[] Commands =
    {
        ("LMB", "Draw / select / drag handles"),
        ("Esc / RMB", "Cancel current draw"),
        ("Delete", "Delete selection"),
        ("Ctrl+Z", "Undo"),
        ("Ctrl+Y / Ctrl+Shift+Z", "Redo"),
        ("Ctrl+S", "Save level (.tres)"),
        ("Ctrl+B", "Bake .tscn"),
        ("+ / −", "Layer up / down"),
        ("Tab", "Cell / corner snap"),
        ("7", "Top-down view toggle"),
        (".", "Frame selection (or everything)"),
        ("Home", "Frame whole level"),
        ("MMB / Shift+MMB", "Orbit / pan camera"),
        ("Mouse wheel", "Zoom"),
        ("F1", "This help"),
    };

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop; // while visible: swallow clicks (and use them to close)

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.AddChild(panel);
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        UiFactory.ApplyMargin(margin, 20);
        panel.AddChild(margin);

        var columns = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        columns.AddThemeConstantOverride("separation", 40);
        margin.AddChild(columns);
        columns.AddChild(BuildColumn("Tools", Tools));
        columns.AddChild(BuildColumn("Commands", Commands));
    }

    public void Toggle() => Visible = !Visible;

    public override void _Input(InputEvent e)
    {
        // Esc closes the sheet. Handled in _Input (before the 3D view's _UnhandledInput) and consumed,
        // so the same press doesn't also cancel an in-progress draw underneath.
        if (Visible && e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Visible = false;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        // Any click on the overlay closes it (F1 toggles too, via the menu accelerator).
        if (e is InputEventMouseButton { Pressed: true })
        {
            Visible = false;
            AcceptEvent();
        }
    }

    private static VBoxContainer BuildColumn(string title, (string Keys, string Action)[] rows)
    {
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };

        var header = new Label { Text = title };
        header.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(header);
        box.AddChild(new HSeparator());

        var grid = new GridContainer { Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 24);
        box.AddChild(grid);

        foreach ((string keys, string action) in rows)
        {
            grid.AddChild(new Label { Text = keys, Modulate = UiConstants.Accent with { A = 1f } });
            grid.AddChild(new Label { Text = action });
        }
        return box;
    }
}
