using System.Collections.Generic;
using Godot;

namespace LevelBuilder.Editor.Gizmos;

/// <summary>
/// Renders the current selection's handles: a widget cube plus a pick collider per handle, the
/// colliders on a dedicated physics layer so the picker can prefer them over bodies. Rebuilt (via
/// EditorContext.Refresh) on selection change, after edits, and every live-drag frame so the widgets
/// track the geometry as it resizes. The dragged handle is held by SelectTool, so these per-frame
/// rebuilds don't disturb it.
///
/// Visibility:
///   • <b>Screen-constant size.</b> Widgets are sized in pixels and re-scaled every frame from the camera
///     distance (or ortho size), so they stay grabbable when zoomed out. Each handle's world size is
///     capped by the distance to its nearest sibling, so closely-spaced handles (a path point's plan /
///     height / remove widgets) never grow into each other, and floored at the old fixed world size so
///     nothing is ever smaller than it used to be.
///   • <b>Outline + depth cue.</b> A dark outline (always on top) separates the widget from any background;
///     the coloured fill is drawn solid where visible and as a translucent ghost where geometry hides it,
///     so you can still grab a far-side handle but can tell it's behind.
///   • <b>Hover.</b> The handle under the mouse grows and brightens, so you know what a click will grab.
/// All three materials sit in the transparent pass so <c>RenderPriority</c> orders them
/// outline → ghost → solid.
/// </summary>
public partial class GizmoLayer : Node3D
{
    /// <summary>Physics layer the handle colliders live on (distinct from bodies on layer 1).</summary>
    public const uint HandleLayer = 2;

    // On-screen sizes (pixels, viewport height basis).
    private const float VisualPx = 14f;
    private const float GrabPx = 24f;
    private const float OutlineFactor = 1.4f;   // outline cube relative to the fill (≈3px border at 14px)
    private const float HoverGrow = 1.3f;

    // World-size floors = the old fixed sizes, so close-up nothing shrinks below what it was.
    private const float MinVisualWorld = 0.16f;
    private const float MinGrabWorld = 0.30f;
    // Fraction of the nearest-sibling distance a widget may occupy (grab < 0.5 → colliders never overlap).
    private const float VisualNeighbourCap = 0.40f;
    private const float GrabNeighbourCap = 0.45f;

    private static readonly Color DefaultColor = new(0.3f, 0.85f, 1.0f);
    private static readonly Color OutlineColor = new(0.04f, 0.05f, 0.07f, 0.85f);
    private static readonly BoxMesh UnitBox = new() { Size = Vector3.One };

    /// <summary>Index of the handle under the mouse, or -1. Injected (picker) so this stays session-free.</summary>
    public System.Func<int> HoveredHandle { get; set; }

    private sealed class Widget
    {
        public Node3D Root;
        public MeshInstance3D Ghost;
        public MeshInstance3D Solid;
        public BoxShape3D Grab;
        public Vector3 Anchor;
        public float StyleScale;
        public float NeighbourDist; // distance to the nearest other handle (∞ if alone)
        public Color Color;
    }

    private readonly List<Widget> _widgets = new();
    private int _hovered = -1;

    public void Rebuild(IReadOnlyList<IEditHandle> handles)
    {
        foreach (Node child in GetChildren())
            child.QueueFree();
        _widgets.Clear();
        _hovered = -1;

        for (int i = 0; i < handles.Count; i++)
        {
            var styled = handles[i] as IStyledHandle;
            var w = new Widget
            {
                Anchor = handles[i].Anchor,
                StyleScale = styled?.WidgetScale ?? 1f,
                Color = styled?.WidgetColor ?? DefaultColor,
                NeighbourDist = float.PositiveInfinity,
            };

            w.Root = new Node3D { Position = w.Anchor };
            AddChild(w.Root);

            w.Root.AddChild(new MeshInstance3D
            {
                Mesh = UnitBox,
                Scale = Vector3.One * OutlineFactor,
                MaterialOverride = Material(OutlineColor, depthTest: false, priority: 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            w.Ghost = new MeshInstance3D
            {
                Mesh = UnitBox,
                MaterialOverride = Material(w.Color with { A = 0.45f }, depthTest: false, priority: 1),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            w.Root.AddChild(w.Ghost);
            w.Solid = new MeshInstance3D
            {
                Mesh = UnitBox,
                MaterialOverride = Material(w.Color, depthTest: true, priority: 2),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            w.Root.AddChild(w.Solid);

            var body = new StaticBody3D
            {
                Position = w.Anchor,
                CollisionLayer = HandleLayer, // detectable by the handle pass
                CollisionMask = 0,            // handles detect nothing themselves
            };
            body.SetMeta("handleIndex", i);
            w.Grab = new BoxShape3D { Size = Vector3.One * MinGrabWorld };
            body.AddChild(new CollisionShape3D { Shape = w.Grab });
            AddChild(body);

            _widgets.Add(w);
        }

        // Nearest-sibling distances (O(n²); n is a handful, or a few hundred for a long path — fine).
        for (int a = 0; a < _widgets.Count; a++)
            for (int b = a + 1; b < _widgets.Count; b++)
            {
                float d = _widgets[a].Anchor.DistanceTo(_widgets[b].Anchor);
                if (d < _widgets[a].NeighbourDist) _widgets[a].NeighbourDist = d;
                if (d < _widgets[b].NeighbourDist) _widgets[b].NeighbourDist = d;
            }

        UpdateSizes(); // size now, so fresh widgets never flash at unit scale for a frame
    }

    public override void _Process(double delta) => UpdateSizes();

    private void UpdateSizes()
    {
        if (_widgets.Count == 0) return;

        int hovered = HoveredHandle?.Invoke() ?? -1;
        if (hovered != _hovered)
        {
            SetHighlight(_hovered, false);
            SetHighlight(hovered, true);
            _hovered = hovered;
        }

        Camera3D cam = GetViewport()?.GetCamera3D();
        float viewportHeight = GetViewport()?.GetVisibleRect().Size.Y ?? 0f;

        for (int i = 0; i < _widgets.Count; i++)
        {
            Widget w = _widgets[i];
            float perPx = WorldPerPixel(cam, viewportHeight, w.Anchor);

            float visual = Fit(VisualPx * perPx, VisualNeighbourCap * w.NeighbourDist, MinVisualWorld);
            float grab = Fit(GrabPx * perPx, GrabNeighbourCap * w.NeighbourDist, MinGrabWorld);
            float hover = i == _hovered ? HoverGrow : 1f;

            w.Root.Scale = Vector3.One * (visual * w.StyleScale * hover);
            Vector3 grabSize = Vector3.One * grab;
            if (!w.Grab.Size.IsEqualApprox(grabSize)) w.Grab.Size = grabSize;
        }
    }

    /// <summary>Screen-derived size, capped by the neighbour room, but never below the old fixed size.</summary>
    private static float Fit(float fromScreen, float neighbourCap, float floor)
        => Mathf.Max(Mathf.Min(fromScreen, neighbourCap), floor);

    /// <summary>World units covered by one viewport pixel at <paramref name="point"/> (0 if unknown → floors apply).</summary>
    private static float WorldPerPixel(Camera3D cam, float viewportHeight, Vector3 point)
    {
        if (cam == null || viewportHeight <= 0f) return 0f;
        if (cam.Projection == Camera3D.ProjectionType.Orthogonal)
            return cam.Size / viewportHeight; // Size is the vertical extent (KeepHeight default)

        float depth = (point - cam.GlobalPosition).Dot(-cam.GlobalBasis.Z);
        if (depth <= 0.01f) return 0f;
        return 2f * depth * Mathf.Tan(Mathf.DegToRad(cam.Fov * 0.5f)) / viewportHeight;
    }

    private void SetHighlight(int index, bool on)
    {
        if (index < 0 || index >= _widgets.Count) return;
        Widget w = _widgets[index];
        Color c = on ? w.Color.Lerp(Colors.White, 0.45f) : w.Color;
        ((StandardMaterial3D)w.Solid.MaterialOverride).AlbedoColor = c;
        ((StandardMaterial3D)w.Ghost.MaterialOverride).AlbedoColor = c with { A = on ? 0.7f : 0.45f };
    }

    private static StandardMaterial3D Material(Color color, bool depthTest, int priority) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = color,
        // Transparent pass for all three layers, so RenderPriority orders them and the depth-tested solid
        // fill still tests against the opaque scene. NoDepthTest layers (outline, ghost) stay visible
        // through geometry: edge handles sit between an opening's placeholder and the intact wall, and
        // far-side instance handles sit behind the body.
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = !depthTest,
        RenderPriority = priority,
    };
}
