using Godot;
using LevelBuilder.Core.Primitives;

namespace LevelBuilder.UI;

/// <summary>
/// Shared widget factories for the UI panels. Centralizes the patterns that were duplicated per
/// panel: focus-safe buttons (FocusMode None so a clicked button can't swallow tool hotkeys),
/// dim section labels, margin setup, native FileDialogs and the short-id formatter.
/// </summary>
public static class UiFactory
{
    /// <summary>Focus-safe push button. Defaults to <see cref="UiConstants.ButtonMin"/>.</summary>
    public static Button MakeButton(string text, System.Action onPressed,
        Vector2? minSize = null, string tooltip = null)
    {
        var button = new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None, // don't let a focused button eat tool hotkeys
            CustomMinimumSize = minSize ?? UiConstants.ButtonMin,
        };
        if (tooltip != null) button.TooltipText = tooltip;
        button.Pressed += onPressed;
        return button;
    }

    /// <summary>Dim section-header label.</summary>
    public static Label Section(string text) => new() { Text = text, Modulate = UiConstants.FontDim };

    /// <summary>Uniform content margin on all four sides (defaults to <see cref="UiConstants.Margin"/>).</summary>
    public static void ApplyMargin(MarginContainer margin, int px = UiConstants.Margin)
    {
        margin.AddThemeConstantOverride("margin_left", px);
        margin.AddThemeConstantOverride("margin_top", px);
        margin.AddThemeConstantOverride("margin_right", px);
        margin.AddThemeConstantOverride("margin_bottom", px);
    }

    /// <summary>
    /// Native filesystem dialog (whole disk, not just res://), parented to <paramref name="owner"/>.
    /// Show with <see cref="ShowDialog"/>.
    /// </summary>
    public static FileDialog MakeFileDialog(Node owner, FileDialog.FileModeEnum mode, string title)
    {
        var dialog = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = mode,
            Title = title,
            UseNativeDialog = true,
        };
        owner.AddChild(dialog);
        return dialog;
    }

    public static void ShowDialog(FileDialog dialog) => dialog.PopupCentered(UiConstants.FileDialogSize);

    /// <summary>
    /// Makes a SpinBox release keyboard focus once a typed value is committed (Enter), so the
    /// tool hotkeys work again without an explicit click elsewhere. (FocusMode None would break
    /// typing into the field — release-on-submit is the right pattern for editable fields.)
    /// </summary>
    public static void ReleaseFocusOnSubmit(SpinBox sb)
        => sb.GetLineEdit().TextSubmitted += _ => sb.GetLineEdit().ReleaseFocus();

    /// <summary>
    /// Human-readable name for a primitive type id ("polygon_floor" → its registry DisplayName, e.g.
    /// "Polygon Floor"); falls back to a prettified id for unknown types.
    /// </summary>
    public static string TypeLabel(PrimitiveRegistry registry, string typeId)
    {
        if (string.IsNullOrEmpty(typeId)) return "Object";
        string name = registry?.Get(typeId)?.DisplayName;
        if (!string.IsNullOrEmpty(name)) return name;
        string spaced = typeId.Replace('_', ' ');
        return $"{char.ToUpperInvariant(spaced[0])}{spaced[1..]}";
    }

    /// <summary>Door vs window label for an opening (a window has a sill above the floor).</summary>
    public static string OpeningLabel(Core.Data.OpeningData o) => o != null && o.SillHeight > 0 ? "Window" : "Door";

    /// <summary>Last <paramref name="len"/> chars of an id — a compact disambiguator for labels.</summary>
    public static string ShortId(string id, int len = 6)
        => string.IsNullOrEmpty(id) ? "?" : id.Length <= len ? id : id[^len..];
}
