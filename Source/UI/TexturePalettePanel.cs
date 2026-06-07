using System.Collections.Generic;
using System.Linq;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.UI;

/// <summary>
/// The "Textures" tab of the bottom dock: the texture library as a grid of draggable swatches,
/// grouped by source (color folder for the bundled pack, "custom" for user-added). Pure drag
/// sources — drag a swatch onto an object (viewport) or the inspector's Texture slot to apply it.
///
/// "Add texture…" lets the user pull in their own images: the file is copied into the project
/// (<see cref="TextureCatalog.UserRoot"/>) so it gets a stable res:// path that save/bake reference,
/// then the grid repopulates. A freshly added texture shows immediately via the raw-decode fallback
/// in <see cref="TextureLoader"/> (no editor reimport needed for the in-app preview).
/// </summary>
public partial class TexturePalettePanel : MarginContainer
{
    private VBoxContainer _rows;
    private FileDialog _dialog;

    public void Setup()
    {
        AddThemeConstantOverride("margin_left", 8);
        AddThemeConstantOverride("margin_top", 8);
        AddThemeConstantOverride("margin_right", 8);
        AddThemeConstantOverride("margin_bottom", 8);

        var outer = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        AddChild(outer);

        var addButton = new Button
        {
            Text = "Add texture…",
            FocusMode = FocusModeEnum.None, // don't let it eat tool hotkeys after a click
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        addButton.Pressed += OpenDialog;
        outer.AddChild(addButton);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        outer.AddChild(scroll);

        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rows);

        Populate();
    }

    /// <summary>Rebuilds the swatch grid from the current catalog (call after adding textures).</summary>
    private void Populate()
    {
        foreach (Node child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        List<TextureItem> items = TextureCatalog.Load();
        if (items.Count == 0)
        {
            _rows.AddChild(new Label
            {
                Text = "No textures yet — use “Add texture…”.",
                Modulate = new Color(1, 1, 1, 0.6f),
            });
            return;
        }

        foreach (IGrouping<string, TextureItem> group in items.GroupBy(i => i.Group).OrderBy(g => g.Key))
        {
            _rows.AddChild(new Label { Text = group.Key, Modulate = new Color(1, 1, 1, 0.6f) });

            var flow = new HFlowContainer();
            _rows.AddChild(flow);

            foreach (TextureItem item in group.OrderBy(i => i.Name))
            {
                var swatch = new TextureSwatch();
                flow.AddChild(swatch);
                swatch.Setup(item);
            }
        }
    }

    private void OpenDialog()
    {
        if (_dialog == null)
        {
            _dialog = new FileDialog
            {
                Access = FileDialog.AccessEnum.Filesystem, // browse the whole disk, not just res://
                FileMode = FileDialog.FileModeEnum.OpenFiles,
                Title = "Add textures",
                UseNativeDialog = true,
            };
            _dialog.AddFilter("*.png,*.jpg,*.jpeg,*.webp,*.bmp,*.tga", "Images");
            _dialog.FilesSelected += OnFilesSelected;
            AddChild(_dialog);
        }
        _dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void OnFilesSelected(string[] paths)
    {
        foreach (string path in paths)
            TextureCatalog.ImportUserTexture(path);
        Populate();
    }
}
