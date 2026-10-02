using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>A collectible bone (Super Corgi Ball), floating <c>height</c> metres above where it's placed.</summary>
public sealed class BoneMarkerPrimitive : MarkerPrimitive
{
    public override string TypeId => "bone";
    public override string DisplayName => "Bone";
    public override string MarkerKind => "bone";

    public override IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("yaw",    "Facing (°)", ParamType.Float, 0.0f, -360f, 360f),
        new ParamSpec("height", "Height",     ParamType.Float, 0.8f, 0f,    50f),
    };

    protected override Color ProxyColor => new(1.0f, 0.85f, 0.3f);

    protected override float Lift(PrimitiveInstanceData data) => GetF(data, "height", 0.8f);

    protected override void BuildProxy(SurfaceTool st, PrimitiveInstanceData data)
    {
        // A dog-bone: a shaft with knobs on each end.
        AddBox(st, new Vector3(-0.35f, -0.07f, -0.07f), new Vector3(0.35f, 0.07f, 0.07f));
        AddBox(st, new Vector3(-0.5f, -0.12f, -0.18f), new Vector3(-0.3f, 0.12f, 0.18f));
        AddBox(st, new Vector3(0.3f, -0.12f, -0.18f), new Vector3(0.5f, 0.12f, 0.18f));
    }

    protected override (Vector3 size, Vector3 centre) PickBox(PrimitiveInstanceData data)
        => (new Vector3(1f, 0.5f, 0.5f), Vector3.Zero);
}
