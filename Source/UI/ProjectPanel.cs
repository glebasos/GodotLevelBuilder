using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Editor.Session;

namespace LevelBuilder.UI;

/// <summary>
/// Project actions — the "Project" tab of the bottom dock. Document-level operations: choosing the
/// workspace folder, New/Open/Save of the editable level, and baking/export. Each button routes
/// through the same <see cref="EditorContext"/>/<see cref="AppConfig"/> path as the hotkeys.
///
/// FocusMode is None on every button so a focused button can't swallow the tool hotkeys (same reason
/// as the primitive palette / scene tree). The level-name field is a deliberate exception — it needs
/// focus to type, and while focused it harmlessly captures the hotkeys.
/// </summary>
public partial class ProjectPanel : MarginContainer
{
    private EditorContext _ctx;
    private AppConfig _config;
    private System.Action _onWorkspaceChanged;
    private System.Action<System.Action> _confirmIfDirty;

    private Label _workspaceLabel;
    private Label _targetLabel;
    private LineEdit _nameEdit;
    private LevelDocument _shownDoc; // the document the name field currently reflects
    private Button _exportButton;
    private CheckBox _mergeExportCheck;
    private FileDialog _workspaceDialog;
    private FileDialog _openDialog;
    private FileDialog _targetDialog;

    public void Setup(EditorContext ctx, AppConfig config, System.Action onWorkspaceChanged,
        System.Action<System.Action> confirmIfDirty = null)
    {
        _ctx = ctx;
        _config = config;
        _onWorkspaceChanged = onWorkspaceChanged;
        _confirmIfDirty = confirmIfDirty;

        UiFactory.ApplyMargin(this);

        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(rows);

        // --- Workspace ---
        rows.AddChild(Section("Workspace"));
        var wsRow = Row(rows);
        _workspaceLabel = new Label
        {
            Modulate = new Color(1, 1, 1, 0.75f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        wsRow.AddChild(_workspaceLabel);
        wsRow.AddChild(UiFactory.MakeButton("Change…", OpenWorkspaceDialog,
            tooltip: "Pick the folder where levels (.tres) and custom textures are stored."));
        UpdateWorkspaceLabel();

        // --- Level (New / Open / Save) ---
        rows.AddChild(Section("Level"));
        var levelRow = Row(rows);
        _nameEdit = new LineEdit
        {
            PlaceholderText = "Level name",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(140, 0),
            Text = _ctx.Document.Name,
        };
        _nameEdit.TextSubmitted += OnNameSubmitted;
        _nameEdit.FocusExited += () =>
        {
            _ctx.RenameDocument(_nameEdit.Text); // clicking away commits too…
            _nameEdit.Text = _ctx.Document.Name;  // …and a blanked field snaps back to the real name
        };
        levelRow.AddChild(_nameEdit);
        levelRow.AddChild(UiFactory.MakeButton("New", () => Confirm(() => _ctx.NewLevel()),
            tooltip: "Start a fresh empty level."));
        levelRow.AddChild(UiFactory.MakeButton("Open…", () => Confirm(ShowOpenDialog),
            tooltip: "Open a saved level (.tres) from the workspace."));
        levelRow.AddChild(UiFactory.MakeButton("Save", SaveLevel,
            tooltip: "Save the editable source (.tres) into the workspace (Ctrl+S)."));

        // --- Local bake (preview, inside this project) ---
        rows.AddChild(Section("Bake (local preview)"));
        var bakeRow = Row(rows);
        bakeRow.AddChild(UiFactory.MakeButton("Bake (per-object)", () => _ctx.BakeToGodot(),
            tooltip: "Bake a .tscn with one MeshInstance3D + collision per primitive (Ctrl+B)."));
        bakeRow.AddChild(UiFactory.MakeButton("Bake Merged Chunk", () => _ctx.BakeMergedToGodot(),
            tooltip: "Bake one merged mesh per material + a single trimesh collision (fewest draw calls)."));

        // --- Export to the target game project ---
        rows.AddChild(Section("Export to game"));
        var targetRow = Row(rows);
        _targetLabel = new Label
        {
            Modulate = new Color(1, 1, 1, 0.75f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        targetRow.AddChild(_targetLabel);
        targetRow.AddChild(UiFactory.MakeButton("Set…", OpenTargetDialog,
            tooltip: "Choose the target Godot game project to export levels into."));
        _exportButton = UiFactory.MakeButton("Export to Game", () => _ctx.ExportToGame(_mergeExportCheck.ButtonPressed),
            tooltip: "Write a self-contained .tscn (textures embedded) into the target project's levels/ folder.");
        targetRow.AddChild(_exportButton);
        UpdateTargetLabel();

        // Checked (default) = merged chunk. Unchecked = per-object tree, so individual pieces stay
        // selectable/movable in the Godot editor (a merged trimesh is one fused, uneditable body).
        _mergeExportCheck = new CheckBox
        {
            Text = "Merge into one chunk (uncheck to keep pieces editable in Godot)",
            ButtonPressed = true,
            FocusMode = Control.FocusModeEnum.None, // don't let the checkbox eat tool hotkeys
        };
        rows.AddChild(_mergeExportCheck);

        rows.AddChild(new Label
        {
            Text = "Export = written into the target project's levels/ folder, textures embedded inline "
                 + "(self-contained, no res:// setup). Merged = one mesh per material + one precise "
                 + "trimesh collision (fewest draw calls; per-object material overrides collapse to "
                 + "per-material). Unmerged = one MeshInstance3D + collision per primitive, fully editable.",
            Modulate = new Color(1, 1, 1, 0.55f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        _shownDoc = _ctx.Document;
        _ctx.Changed += OnContextChanged; // keep the name field in sync after New/Open
    }

    public override void _ExitTree()
    {
        if (_ctx != null) _ctx.Changed -= OnContextChanged;
    }

    // ---- level name / save ----------------------------------------------

    private void OnNameSubmitted(string text)
    {
        _ctx.RenameDocument(text); // document metadata, not undo-tracked geometry; fires Changed → title
        _nameEdit.ReleaseFocus();
    }

    private void SaveLevel()
    {
        _ctx.RenameDocument(_nameEdit.Text); // commit a typed-but-not-submitted name
        _ctx.SaveSource();
    }

    private void OnContextChanged()
    {
        // Document swapped (New/Open): always take the new name and drop focus. The buttons are
        // FocusMode None, so the field can still be focused here — without this, its stale text would
        // be committed onto the NEW document on the next focus loss (and Save names the file after it).
        // Text is set before ReleaseFocus so the resulting FocusExited commit is a no-op.
        if (!ReferenceEquals(_ctx.Document, _shownDoc))
        {
            _shownDoc = _ctx.Document;
            _nameEdit.Text = _ctx.Document.Name;
            if (_nameEdit.HasFocus()) _nameEdit.ReleaseFocus();
            return;
        }

        // Same document: reflect its name, but don't clobber what the user is typing.
        if (!_nameEdit.HasFocus() && _nameEdit.Text != _ctx.Document.Name)
            _nameEdit.Text = _ctx.Document.Name;
    }

    /// <summary>Unsaved-changes guard: runs <paramref name="action"/> via Main's confirm dialog when wired.</summary>
    private void Confirm(System.Action action)
    {
        if (_confirmIfDirty != null) _confirmIfDirty(action);
        else action();
    }

    /// <summary>Shows the open-level dialog (also reachable from the File menu).</summary>
    public void ShowOpenDialog()
    {
        if (_openDialog == null)
        {
            _openDialog = UiFactory.MakeFileDialog(this, FileDialog.FileModeEnum.OpenFile, "Open level");
            _openDialog.AddFilter("*.tres", "Level source");
            _openDialog.FileSelected += path => _ctx.OpenLevel(path);
        }
        if (Workspace.IsSet) _openDialog.CurrentDir = Workspace.LevelsDir;
        UiFactory.ShowDialog(_openDialog);
    }

    // ---- target game project --------------------------------------------

    private void OpenTargetDialog()
    {
        if (_targetDialog == null)
        {
            _targetDialog = UiFactory.MakeFileDialog(this, FileDialog.FileModeEnum.OpenDir,
                "Choose the target Godot game project folder");
            _targetDialog.DirSelected += OnTargetChosen;
        }
        UiFactory.ShowDialog(_targetDialog);
    }

    private void OnTargetChosen(string dir)
    {
        _config.TargetProjectPath = dir;
        _config.Save();
        UpdateTargetLabel();
    }

    private void UpdateTargetLabel()
    {
        _targetLabel.Text = _config.HasTarget ? _config.TargetProjectPath : "(no target project set)";
        _exportButton.Disabled = !_config.HasTarget;
    }

    // ---- workspace -------------------------------------------------------

    private void OpenWorkspaceDialog()
    {
        if (_workspaceDialog == null)
        {
            _workspaceDialog = UiFactory.MakeFileDialog(this, FileDialog.FileModeEnum.OpenDir,
                "Choose a workspace folder");
            _workspaceDialog.DirSelected += OnWorkspaceChosen;
        }
        UiFactory.ShowDialog(_workspaceDialog);
    }

    private void OnWorkspaceChosen(string dir)
    {
        _config.WorkspacePath = dir;
        _config.Save();
        Workspace.SetRoot(dir);
        UpdateWorkspaceLabel();
        _onWorkspaceChanged?.Invoke(); // repopulate the texture palette etc.
    }

    private void UpdateWorkspaceLabel() =>
        _workspaceLabel.Text = _config.HasWorkspace ? _config.WorkspacePath : "(not set — pick a folder)";

    // ---- helpers ---------------------------------------------------------

    private static Label Section(string text) => UiFactory.Section(text);

    private static HFlowContainer Row(Node parent)
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(flow);
        return flow;
    }
}
