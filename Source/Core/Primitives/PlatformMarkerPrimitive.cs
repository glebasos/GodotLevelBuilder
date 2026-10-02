using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A moving / spinning platform (Super Corgi Ball): a floor slab (top at the marker, extending down by
/// <c>thickness</c>) that the game turns into a moving StaticBody3D (the ball is carried positionally). It eases from its placed pose to
/// <c>move</c> (marker-local metres, so it follows the facing) and back every <c>period</c> seconds, and
/// spins at <c>spin</c> degrees/s about its own up axis. <c>phase</c> (0..1) offsets the cycle so
/// neighbours can be staggered. The editor shows the travel as a rod to a small cube at the far end.
/// </summary>
public sealed class PlatformMarkerPrimitive : GeometryMarkerPrimitive
{
    private static readonly FloorPrimitive Slab = new();

    public override string TypeId => "platform";
    public override string DisplayName => "Moving Platform";
    public override string MarkerKind => "platform";
    protected override IPrimitive Shape => Slab;

    public override IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("yaw",       "Facing (°)",     ParamType.Float, 0.0f,  -360f, 360f),
        new ParamSpec("width",     "Width",          ParamType.Float, 4.0f,  0.1f,  200f),
        new ParamSpec("depth",     "Depth",          ParamType.Float, 4.0f,  0.1f,  200f),
        new ParamSpec("thickness", "Thickness",      ParamType.Float, 0.3f,  0.05f, 20f),
        new ParamSpec("moveX",     "Move X (local)", ParamType.Float, 0.0f,  -500f, 500f),
        new ParamSpec("moveY",     "Move Y",         ParamType.Float, 0.0f,  -500f, 500f),
        new ParamSpec("moveZ",     "Move Z (local)", ParamType.Float, -8.0f, -500f, 500f),
        new ParamSpec("period",    "Round trip (s)", ParamType.Float, 6.0f,  0.5f,  600f),
        new ParamSpec("spin",      "Spin (°/s)",     ParamType.Float, 0.0f,  -720f, 720f),
        new ParamSpec("phase",     "Phase (0-1)",    ParamType.Float, 0.0f,  0f,    1f),
    };

    protected override Color ProxyColor => new(0.3f, 0.95f, 1.0f);

    public override Vector3 TravelOffset(PrimitiveInstanceData data)
        => new(GetF(data, "moveX", 0f), GetF(data, "moveY", 0f), GetF(data, "moveZ", -8f));

    protected override bool HasProxy(PrimitiveInstanceData data) => TravelOffset(data).LengthSquared() > 0.0001f;

    protected override void BuildProxy(SurfaceTool st, PrimitiveInstanceData data)
    {
        // A thin rod from the slab centre to the travel end, and a small cube where the centre arrives.
        Vector3 end = TravelOffset(data);
        float len = end.Length();
        Vector3 dir = end / len;
        Vector3 up = Mathf.Abs(dir.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        var rod = new Transform3D(Basis.LookingAt(dir, up), Vector3.Zero);
        AddBoxTransformed(st, rod, new Vector3(-0.05f, -0.05f, -len), new Vector3(0.05f, 0.05f, 0f));
        AddBox(st, end - new Vector3(0.3f, 0.3f, 0.3f), end + new Vector3(0.3f, 0.3f, 0.3f));
    }

    public override IEnumerable<(string key, Variant value)> BakeMeta(PrimitiveInstanceData data)
    {
        yield return ("move", TravelOffset(data));
        yield return ("period", GetF(data, "period", 6f));
        yield return ("spin", GetF(data, "spin", 0f));
        yield return ("phase", GetF(data, "phase", 0f));
    }

    private static void AddBoxTransformed(SurfaceTool st, Transform3D xform, Vector3 min, Vector3 max)
    {
        var tmp = new SurfaceTool();
        tmp.Begin(Mesh.PrimitiveType.Triangles);
        AddBox(tmp, min, max);
        var mesh = new ArrayMesh();
        tmp.Commit(mesh);
        st.AppendFrom(mesh, 0, xform);
    }
}
