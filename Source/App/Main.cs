using Godot;
using LevelBuilder.Core;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Primitives;
using LevelBuilder.Editor.Camera;
using LevelBuilder.Editor.Commands;
using LevelBuilder.Editor.Gizmos;
using LevelBuilder.Editor.Grid;
using LevelBuilder.Editor.Session;
using LevelBuilder.Editor.Tools;
using LevelBuilder.Editor.View;
using LevelBuilder.UI;

namespace LevelBuilder.App;

/// <summary>
/// Editor shell root. Builds an in-memory document with one storey, wires up the grid,
/// camera, cursor, live view, command stack and tools.
///
/// Layout: the 3D world lives in its own <see cref="SubViewport"/> so the docked UI can
/// shrink the viewport rather than overlap it. The scene-tree panel docks to its left via
/// an <see cref="HSplitContainer"/>; mouse + keyboard reach the 3D nodes through the
/// container's input forwarding.
///
/// M2: draw floors (F) and walls (W) on the grid; undo/redo with Ctrl+Z / Ctrl+Y.
/// </summary>
public partial class Main : Node3D
{
    private EditorContext _ctx;
    private Control _uiRoot;
    private ConfirmationDialog _confirmDialog;
    private System.Action _pendingAction; // what to run once the unsaved-changes dialog resolves

    public override void _Ready()
    {
        GD.Print("=== LevelBuilder — editor shell (docked scene tree) ===");

        // App config + workspace: the writable home for levels and custom textures, chosen by the
        // user (res:// is read-only once the builder is exported as a binary). Restored from
        // user://levelbuilder.cfg; the static Workspace pointer drives the texture path helpers.
        AppConfig config = AppConfig.Load();
        if (config.HasWorkspace) Workspace.SetRoot(config.WorkspacePath);

        LevelDocument doc = NewDocument(out StoreyData storey);
        PrimitiveRegistry registry = PrimitiveRegistry.CreateDefault();

        var grid = new GridRenderer { CellSize = doc.Grid.CellSize };
        var levelView = new LevelView();
        levelView.Setup(doc, registry);
        var previewLayer = new Node3D { Name = "PreviewLayer" };
        var cursor = new GridCursor { CellSize = doc.Grid.CellSize, Elevation = storey.BaseElevation };
        var cameraRig = new EditorCameraRig();
        var picker = new InstancePicker();
        var gizmos = new GizmoLayer { Name = "GizmoLayer" };
        var pathOverlay = new PathOverlay { Name = "PathOverlay" };
        var tools = new ToolManager();

        // The 3D world renders into a SubViewport (its own World3D + physics space), so the
        // docked panel can take screen space without occluding the view.
        var viewport = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        viewport.AddChild(grid);
        viewport.AddChild(levelView);
        viewport.AddChild(previewLayer);
        viewport.AddChild(gizmos);
        viewport.AddChild(pathOverlay);
        viewport.AddChild(cursor);     // before ToolManager so HoveredCell/Corner is fresh each frame
        viewport.AddChild(cameraRig);
        viewport.AddChild(picker);
        viewport.AddChild(tools);
        viewport.AddChild(BuildSunLight());
        viewport.AddChild(BuildEnvironment());

        // Stretch makes the SubViewport track the container's size, which is what keeps the
        // mouse-to-camera projection correct even though the viewport is offset by the panel.
        // The container also accepts dropped texture swatches (raycasts to the object under the drop).
        var viewportContainer = new ViewportDropContainer
        {
            Stretch = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        viewportContainer.AddChild(viewport);

        var sceneTree = new SceneTreePanel();
        var inspector = new InspectorPanel();

        // Top row: scene-tree | (3D view | inspector). Nested split so the viewport expands
        // while both side docks keep their width.
        var rightSplit = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightSplit.AddChild(viewportContainer); // expands to fill
        rightSplit.AddChild(inspector);         // right dock (fixed width)

        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddChild(sceneTree);  // left dock
        split.AddChild(rightSplit); // viewport + inspector

        // Bottom dock: tabbed — primitive palette + texture library + project actions.
        var palette = new PrimitivePalettePanel { Name = "Primitives" };
        var textures = new TexturePalettePanel { Name = "Textures" };
        var project = new ProjectPanel { Name = "Project" };
        var bottomTabs = new TabContainer { CustomMinimumSize = new Vector2(0, UiConstants.BottomDockHeight) };
        bottomTabs.AddChild(palette);
        bottomTabs.AddChild(textures);
        bottomTabs.AddChild(project);

        var outer = new VSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        outer.AddChild(split);       // top: viewport row (expands)
        outer.AddChild(bottomTabs);  // bottom: tabbed dock

        // App shell: menu bar (top) / split layout (middle) / status bar (bottom), all inside one
        // themed root Control. The Theme propagates to every panel; the SubViewport's rendered 3D
        // image is unaffected (Theme only touches Control drawing — never Modulate the container).
        var menuBar = new MenuBarPanel();
        var statusBar = new StatusBar();
        var shell = new VBoxContainer();
        shell.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        shell.AddChild(menuBar);
        shell.AddChild(outer);
        shell.AddChild(statusBar);

        var uiRoot = new Control { Name = "UiRoot", Theme = UiTheme.Build() };
        uiRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        uiRoot.AddChild(shell);
        _uiRoot = uiRoot;

        // Overlays above the shell: toasts (never block input) + the F1 help sheet — inherit the theme.
        var toasts = new ToastLayer { Name = "Toasts" };
        var helpOverlay = new HelpOverlay { Name = "Help" };
        uiRoot.AddChild(toasts);
        uiRoot.AddChild(helpOverlay);
        AddChild(uiRoot);

        var ctx = new EditorContext
        {
            Registry = registry,
            Commands = new CommandStack(),
            View = levelView,
            Cursor = cursor,
            Grid = grid,
            PreviewLayer = previewLayer,
            Picker = picker,
            Gizmos = gizmos,
            PathOverlay = pathOverlay,
            Config = config,
        };
        _ctx = ctx;
        ctx.ReplaceDocument(doc);    // set the initial document BEFORE panels read ctx.Document in their Setup
        sceneTree.Setup(ctx);        // panels self-populate here and subscribe for later Changed events
        inspector.Setup(ctx);
        viewportContainer.Setup(viewport, ctx.AssignTextureFromDrop); // drop a swatch onto an object (whole selection if it's part of one)
        tools.Setup(ctx);
        ctx.CancelActiveTool = tools.CancelActive; // so a document swap cancels a half-drawn primitive (and a height change)
        ctx.Notified += toasts.Show;               // save/bake/export feedback as toasts (console keeps its mirror)

        // Draw-height indicator: a corner Control over the 3D view (not inside the SubViewport). Added
        // AFTER the drop overlay so it stays the topmost child and its scrub drag isn't intercepted.
        var heightIndicator = new HeightIndicatorPanel();
        viewportContainer.AddChild(heightIndicator);
        heightIndicator.Setup(ctx);
        palette.Setup(registry, tools, ctx); // after tools.Setup so the primitive->tool map exists
        textures.Setup();
        project.Setup(ctx, config, textures.Refresh, ConfirmIfDirty); // Change-workspace repopulates the texture palette
        statusBar.Setup(ctx, tools);
        menuBar.Setup(ctx,
            requestNew: () => ConfirmIfDirty(() => ctx.NewLevel()),
            requestOpen: () => ConfirmIfDirty(project.ShowOpenDialog),
            requestQuit: RequestQuit,
            toggleTopDown: cameraRig.ToggleTopDown,
            toggleHelp: helpOverlay.Toggle);

        // Intercept window close so unsaved work prompts instead of silently quitting.
        GetTree().AutoAcceptQuit = false;

        // Window title tracks the open level + a dirty marker ("LevelBuilder — Name*").
        System.Action updateTitle = () =>
            GetWindow().Title = $"LevelBuilder — {ctx.Document.Name}{(ctx.IsDirty ? "*" : "")}";
        ctx.Changed += updateTitle;            // document swap / rename
        ctx.Commands.DirtyChanged += updateTitle; // edit / undo / save
        updateTitle();

        // Resume where we left off: reopen the last saved level if it still exists.
        if (config.HasWorkspace && !string.IsNullOrEmpty(config.LastLevelPath)
            && FileAccess.FileExists(config.LastLevelPath))
            ctx.OpenLevel(config.LastLevelPath);
    }

    public override void _Notification(int what)
    {
        // AutoAcceptQuit is off: the X button lands here so unsaved work can prompt first.
        if (what == NotificationWMCloseRequest) RequestQuit();
    }

    private void RequestQuit() => ConfirmIfDirty(() => GetTree().Quit());

    /// <summary>
    /// Runs <paramref name="proceed"/> immediately when there are no unsaved changes; otherwise
    /// shows a Save / Discard / Cancel dialog first. Shared by window-close, the File menu and the
    /// Project tab's New/Open.
    /// </summary>
    private void ConfirmIfDirty(System.Action proceed)
    {
        if (!_ctx.IsDirty) { proceed(); return; }

        if (_confirmDialog == null)
        {
            _confirmDialog = new ConfirmationDialog
            {
                Title = "Unsaved changes",
                DialogText = "The level has unsaved changes.",
                OkButtonText = "Save",
            };
            _confirmDialog.AddButton("Discard", true, "discard");
            _confirmDialog.Confirmed += () =>
            {
                _ctx.SaveSource();
                System.Action pending = _pendingAction;
                _pendingAction = null;
                if (!_ctx.IsDirty) pending?.Invoke(); // save can fail (e.g. no workspace) — then stay put
            };
            _confirmDialog.CustomAction += action =>
            {
                if (action != "discard") return;
                _confirmDialog.Hide();
                System.Action pending = _pendingAction;
                _pendingAction = null;
                pending?.Invoke();
            };
            _confirmDialog.Canceled += () => _pendingAction = null;
            _uiRoot.AddChild(_confirmDialog);
        }

        _pendingAction = proceed;
        _confirmDialog.PopupCentered();
    }

    private static LevelDocument NewDocument(out StoreyData storey)
    {
        storey = new StoreyData { Id = Ids.New(), Name = "Ground Floor", BaseElevation = 0f, Height = 3f };
        var doc = new LevelDocument { Name = "Untitled" };
        DefaultMaterials.Seed(doc.Materials);
        doc.Storeys.Add(storey);
        return doc;
    }

    private static DirectionalLight3D BuildSunLight()
    {
        var light = new DirectionalLight3D { ShadowEnabled = true };
        light.RotationDegrees = new Vector3(-55, -45, 0);
        return light;
    }

    private static WorldEnvironment BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.16f, 0.17f, 0.19f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.57f, 0.62f),
            AmbientLightEnergy = 0.4f,
        };
        return new WorldEnvironment { Environment = env };
    }
}
