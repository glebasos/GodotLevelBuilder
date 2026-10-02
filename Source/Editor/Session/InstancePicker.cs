using Godot;

namespace LevelBuilder.Editor.Session;

/// <summary>Outcome of a viewport pick: a gizmo handle, an instance, or an opening.</summary>
public readonly struct PickResult
{
    public bool Hit { get; }
    public string InstanceId { get; }
    /// <summary>Set when an opening's pick box was hit; <see cref="InstanceId"/> is then its owning wall.</summary>
    public string OpeningId { get; }
    /// <summary>Index into the current handle list when a gizmo handle was hit, else -1.</summary>
    public int HandleIndex { get; }
    public Vector3 Position { get; }

    public PickResult(string instanceId, Vector3 position, string openingId = null)
    {
        Hit = true;
        InstanceId = instanceId;
        OpeningId = openingId;
        HandleIndex = -1;
        Position = position;
    }

    private PickResult(int handleIndex)
    {
        Hit = true;
        InstanceId = null;
        OpeningId = null;
        HandleIndex = handleIndex;
        Position = Vector3.Zero;
    }

    public static PickResult Handle(int index) => new(index);

    public bool IsOpening => !string.IsNullOrEmpty(OpeningId);
    public bool IsHandle => HandleIndex >= 0;
}

/// <summary>
/// Resolves which primitive instance is under the mouse by raycasting from the active
/// camera against the per-instance pick colliders the LevelView builds. Each pick body
/// carries an "instanceId" metadata. The raycast runs in _PhysicsProcess (where physics
/// queries are valid) and the latest result is cached, so callers can read it any time.
/// </summary>
public partial class InstancePicker : Node3D
{
    private const float RayLength = 2000f;
    private PickResult _latest;

    /// <summary>The most recent pick (from the last physics frame).</summary>
    public PickResult Pick() => _latest;

    /// <summary>Current mouse position in viewport pixels (for screen-space drag thresholds).</summary>
    public Vector2 MouseScreen() => GetViewport().GetMousePosition();

    /// <summary>Projects a world point to viewport pixels. False if there's no camera or the point is behind it.</summary>
    public bool WorldToScreen(Vector3 world, out Vector2 screen)
    {
        screen = Vector2.Zero;
        Camera3D cam = GetViewport().GetCamera3D();
        if (cam == null || cam.IsPositionBehind(world)) return false;
        screen = cam.UnprojectPosition(world);
        return true;
    }

    /// <summary>The current mouse ray (world origin + normalized direction); false if no camera.</summary>
    public bool MouseRay(out Vector3 from, out Vector3 dir)
    {
        from = Vector3.Zero;
        dir = Vector3.Zero;
        Camera3D cam = GetViewport().GetCamera3D();
        if (cam == null) return false;

        Vector2 mouse = GetViewport().GetMousePosition();
        from = cam.ProjectRayOrigin(mouse);
        dir = cam.ProjectRayNormal(mouse);
        return true;
    }

    public override void _PhysicsProcess(double delta) => _latest = Raycast();

    private const uint BodyMask = 1;   // instance pick colliders (LevelView, default layer)
    /// <summary>Opening pick boxes live on their own layer so a piece INSIDE a doorway (e.g. a door panel)
    /// can win over the opening box that encloses it. See <see cref="Raycast"/>.</summary>
    public const uint OpeningLayer = 4;
    private const float OpeningYield = 0.5f; // an instance this close behind the opening's face wins
    private const uint HandleMask = Gizmos.GizmoLayer.HandleLayer; // gizmo handle colliders

    private PickResult Raycast()
    {
        Camera3D cam = GetViewport().GetCamera3D();
        if (cam == null) return default;

        Vector2 mouse = GetViewport().GetMousePosition();
        Vector3 from = cam.ProjectRayOrigin(mouse);
        Vector3 to = from + cam.ProjectRayNormal(mouse) * RayLength;
        PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;

        // Handles win over the bodies they sit on: a masked handle-only pass first.
        var handleQuery = PhysicsRayQueryParameters3D.Create(from, to);
        handleQuery.CollisionMask = HandleMask;
        Godot.Collections.Dictionary handleHit = space.IntersectRay(handleQuery);
        if (handleHit.Count > 0)
        {
            var hc = handleHit["collider"].As<Node>();
            if (hc != null && hc.HasMeta("handleIndex"))
                return PickResult.Handle(hc.GetMeta("handleIndex").AsInt32());
        }

        var bodyQuery = PhysicsRayQueryParameters3D.Create(from, to);
        bodyQuery.CollisionMask = BodyMask;
        Godot.Collections.Dictionary bodyHit = space.IntersectRay(bodyQuery);

        var openingQuery = PhysicsRayQueryParameters3D.Create(from, to);
        openingQuery.CollisionMask = OpeningLayer;
        Godot.Collections.Dictionary openingHit = space.IntersectRay(openingQuery);

        // The opening wins unless an instance is hit within the doorway's depth: that's something sitting
        // in the opening (a door panel), which must stay selectable. Through an empty doorway the next
        // instance is the floor/wall far behind, so the opening still wins there.
        Godot.Collections.Dictionary hit = bodyHit;
        if (openingHit.Count > 0)
        {
            float openingDist = from.DistanceTo(openingHit["position"].AsVector3());
            bool instanceInside = bodyHit.Count > 0
                && from.DistanceTo(bodyHit["position"].AsVector3()) <= openingDist + OpeningYield;
            if (!instanceInside) hit = openingHit;
        }
        if (hit.Count == 0) return default;

        var collider = hit["collider"].As<Node>();
        if (collider == null || !collider.HasMeta("instanceId")) return default;

        string openingId = collider.HasMeta("openingId") ? collider.GetMeta("openingId").AsString() : null;
        return new PickResult(collider.GetMeta("instanceId").AsString(), hit["position"].AsVector3(), openingId);
    }
}
