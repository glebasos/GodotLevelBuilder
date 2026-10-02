using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// Where the ball starts (Super Corgi Ball). Exactly one per level. The ball is dropped from
/// <c>drop</c> metres above the marker, and the camera starts behind it looking along the arrow.
/// Also carries the level-wide settings (time limit, fall-out depth) so they live on one object
/// the inspector can already edit.
/// </summary>
public sealed class SpawnMarkerPrimitive : MarkerPrimitive
{
    public override string TypeId => "spawn";
    public override string DisplayName => "Start";
    public override string MarkerKind => "spawn";

    public override IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("yaw",       "Facing (°)",           ParamType.Float, 0.0f,  -360f, 360f),
        new ParamSpec("drop",      "Drop height",          ParamType.Float, 6.0f,  0f,    100f),
        new ParamSpec("timeLimit", "Time limit (s)",       ParamType.Float, 60.0f, 0f,    3600f),
        new ParamSpec("fallDepth", "Fall-out depth",       ParamType.Float, 10.0f, 1f,    1000f),
    };

    protected override Color ProxyColor => new(0.25f, 0.55f, 1.0f);

    protected override void BuildProxy(SurfaceTool st, PrimitiveInstanceData data)
    {
        // A pad on the floor, an arrow for the facing, and a post up to the drop height.
        AddBox(st, new Vector3(-0.6f, 0f, -0.6f), new Vector3(0.6f, 0.05f, 0.6f));
        AddFacingArrow(st, 1.8f, 0.05f);
        float drop = GetF(data, "drop", 6f);
        AddBox(st, new Vector3(-0.05f, 0f, -0.05f), new Vector3(0.05f, drop, 0.05f));
        AddBox(st, new Vector3(-0.3f, drop - 0.3f, -0.3f), new Vector3(0.3f, drop + 0.3f, 0.3f));
    }

    protected override (Vector3 size, Vector3 centre) PickBox(PrimitiveInstanceData data)
        => (new Vector3(1.2f, 0.4f, 1.2f), new Vector3(0, 0.2f, 0));

    public override IEnumerable<(string key, Variant value)> BakeMeta(PrimitiveInstanceData data)
    {
        yield return ("drop", GetF(data, "drop", 6f));
    }
}
