using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A conveyor / boost zone (Super Corgi Ball): a <c>width</c> x <c>depth</c> area, placed on top of a
/// floor, that drives the ball toward <c>speed</c> m/s along the facing (local −Z; negative = backwards).
/// Not geometry: the game draws scrolling chevrons over it and pushes the ball while it's inside.
/// </summary>
public sealed class ConveyorMarkerPrimitive : MarkerPrimitive
{
    public override string TypeId => "conveyor";
    public override string DisplayName => "Conveyor";
    public override string MarkerKind => "conveyor";

    public override IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("yaw",   "Facing (°)",  ParamType.Float, 0.0f, -360f, 360f),
        new ParamSpec("width", "Width",       ParamType.Float, 3.0f, 0.5f,  200f),
        new ParamSpec("depth", "Length",      ParamType.Float, 6.0f, 0.5f,  200f),
        new ParamSpec("speed", "Speed (m/s)", ParamType.Float, 8.0f, -60f,  60f),
    };

    protected override Color ProxyColor => new(0.65f, 0.4f, 1.0f);

    protected override void BuildProxy(SurfaceTool st, PrimitiveInstanceData data)
    {
        float hw = GetF(data, "width", 3f) * 0.5f, hd = GetF(data, "depth", 6f) * 0.5f;
        const float r = 0.08f, h = 0.04f;

        // Thin rim around the zone.
        AddBox(st, new Vector3(-hw, 0, -hd), new Vector3(hw, h, -hd + r));
        AddBox(st, new Vector3(-hw, 0, hd - r), new Vector3(hw, h, hd));
        AddBox(st, new Vector3(-hw, 0, -hd), new Vector3(-hw + r, h, hd));
        AddBox(st, new Vector3(hw - r, 0, -hd), new Vector3(hw, h, hd));

        // A row of arrows pointing the way the belt pushes (−Z for positive speed).
        bool forward = GetF(data, "speed", 8f) >= 0;
        for (float z = hd - 1.0f; z >= -hd + 1.0f; z -= 2f)
        {
            AddBox(st, new Vector3(-0.08f, 0, z - 0.5f), new Vector3(0.08f, h, z + 0.5f));
            float headZ = forward ? z - 0.5f : z + 0.5f;
            AddBox(st, new Vector3(-0.35f, 0, headZ - 0.12f), new Vector3(0.35f, h, headZ + 0.12f));
        }
    }

    protected override (Vector3 size, Vector3 centre) PickBox(PrimitiveInstanceData data)
        => (new Vector3(GetF(data, "width", 3f), 0.2f, GetF(data, "depth", 6f)), new Vector3(0, 0.1f, 0));

    public override IEnumerable<(string key, Variant value)> BakeMeta(PrimitiveInstanceData data)
    {
        yield return ("size", new Vector2(GetF(data, "width", 3f), GetF(data, "depth", 6f)));
        yield return ("speed", GetF(data, "speed", 8f));
    }
}
