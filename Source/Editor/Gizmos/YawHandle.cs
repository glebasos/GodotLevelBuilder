using System;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Editor.Commands;

namespace LevelBuilder.Editor.Gizmos;

/// <summary>
/// Turns a gameplay marker's <c>yaw</c> parameter (Start, Goal, platforms …). A pink widget sits at the end
/// of an arm along the marker's facing (local −Z rotated by yaw); dragging it sweeps the cursor around the
/// marker on the horizontal plane and the swept angle is added to the yaw, snapped to 15°. One
/// <see cref="SetParameterCommand"/> per drag, like the other handles.
/// </summary>
public sealed class YawHandle : IEditHandle, IStyledHandle
{
    private const float SnapDeg = 15f;
    private const float Lift = 0.3f;

    private readonly PrimitiveInstanceData _inst;
    private readonly Vector3 _centre;
    private readonly float _origDeg;
    private readonly Variant _origValue;
    private float _deg;

    public YawHandle(PrimitiveInstanceData inst, Vector3 worldCentre, float arm)
    {
        _inst = inst;
        _origValue = inst.Parameters.ContainsKey("yaw") ? inst.Parameters["yaw"] : (double)0f;
        _origDeg = _origValue.AsSingle();
        _deg = _origDeg;
        _centre = worldCentre + Vector3.Up * Lift;
        Anchor = _centre + Facing(_origDeg) * arm;
    }

    public Color WidgetColor => new(1.0f, 0.35f, 0.75f);
    public float WidgetScale => 0.9f;

    public Vector3 Anchor { get; }

    public bool Grab(Vector3 rayFrom, Vector3 rayDir, out Vector3 world)
        => GizmoMath.RayPlane(rayFrom, rayDir, _centre, Vector3.Up, out world);

    public void Preview(Vector3 grabStart, Vector3 grabNow)
    {
        Vector3 s = Flat(grabStart - _centre), n = Flat(grabNow - _centre);
        if (s.LengthSquared() < 1e-4f || n.LengthSquared() < 1e-4f) return;

        // Signed angle about +Y from the press direction to the current one — the same sense as the
        // yaw rotation (Basis(Up, θ)), so the widget follows the cursor.
        float add = Mathf.RadToDeg(Mathf.Atan2(s.Cross(n).Dot(Vector3.Up), s.Dot(n)));
        float deg = Mathf.Round((_origDeg + add) / SnapDeg) * SnapDeg;
        _deg = Mathf.Wrap(deg, -180f, 180f);
        _inst.Parameters["yaw"] = (double)_deg;
    }

    public void Cancel()
    {
        _inst.Parameters["yaw"] = _origValue;
        _deg = _origDeg;
    }

    public bool Changed => !Mathf.IsEqualApprox(_deg, _origDeg);

    public ICommand Commit(Action refresh)
        => new SetParameterCommand(_inst, "yaw", _origValue, (double)_deg, refresh);

    /// <summary>World facing of a marker at <paramref name="deg"/> yaw: local −Z turned about +Y.</summary>
    public static Vector3 Facing(float deg) => new Basis(Vector3.Up, Mathf.DegToRad(deg)) * Vector3.Forward;

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
}
