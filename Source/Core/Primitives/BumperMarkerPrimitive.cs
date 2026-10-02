using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A bumper post (Super Corgi Ball): a cylinder standing on the marker that kicks the ball away
/// (horizontally, in the stage's frame) at <c>strength</c> m/s when it touches.
/// </summary>
public sealed class BumperMarkerPrimitive : GeometryMarkerPrimitive
{
    private static readonly CylinderPrimitive Post = new();

    public override string TypeId => "bumper";
    public override string DisplayName => "Bumper";
    public override string MarkerKind => "bumper";
    protected override IPrimitive Shape => Post;

    public override IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("yaw",      "Facing (°)", ParamType.Float, 0.0f,  -360f, 360f),
        new ParamSpec("radius",   "Radius",     ParamType.Float, 0.8f,  0.1f,  50f),
        new ParamSpec("height",   "Height",     ParamType.Float, 1.0f,  0.05f, 50f),
        new ParamSpec("sides",    "Sides",      ParamType.Int,   20,    3f,    128f),
        new ParamSpec("strength", "Kick (m/s)", ParamType.Float, 12.0f, 0f,    100f),
    };

    protected override Color ProxyColor => new(1f, 0.5f, 0.1f);

    public override IEnumerable<(string key, Variant value)> BakeMeta(PrimitiveInstanceData data)
    {
        yield return ("strength", GetF(data, "strength", 12f));
    }
}
