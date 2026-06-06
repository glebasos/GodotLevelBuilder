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
    public override void _Ready()
    {
        GD.Print("=== LevelBuilder — editor shell (docked scene tree) ===");

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
        var tools = new ToolManager();

        // The 3D world renders into a SubViewport (its own World3D + physics space), so the
        // docked panel can take screen space without occluding the view.
        var viewport = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        viewport.AddChild(grid);
        viewport.AddChild(levelView);
        viewport.AddChild(previewLayer);
        viewport.AddChild(gizmos);
        viewport.AddChild(cursor);     // before ToolManager so HoveredCell/Corner is fresh each frame
        viewport.AddChild(cameraRig);
        viewport.AddChild(picker);
        viewport.AddChild(tools);
        viewport.AddChild(BuildSunLight());
        viewport.AddChild(BuildEnvironment());

        // Stretch makes the SubViewport track the container's size, which is what keeps the
        // mouse-to-camera projection correct even though the viewport is offset by the panel.
        var viewportContainer = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        viewportContainer.AddChild(viewport);

        var sceneTree = new SceneTreePanel();

        var split = new HSplitContainer();
        split.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        split.AddChild(sceneTree);          // left: docked panel
        split.AddChild(viewportContainer);  // right: 3D view (expands to fill)
        AddChild(split);

        var ctx = new EditorContext
        {
            Document = doc,
            Registry = registry,
            Commands = new CommandStack(),
            View = levelView,
            Cursor = cursor,
            Grid = grid,
            PreviewLayer = previewLayer,
            Picker = picker,
            Gizmos = gizmos,
        };
        sceneTree.Setup(ctx);        // subscribe before the first Changed fires below
        ctx.SetActiveStorey(storey); // unified path: positions grid + cursor at the storey's elevation
        tools.Setup(ctx);
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
