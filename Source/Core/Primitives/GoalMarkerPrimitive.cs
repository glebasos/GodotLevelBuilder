using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// The finish gate (Super Corgi Ball). The proxy approximates the game's FinishGate: two posts
/// ~5 m apart and a crossbar, the ball passing through along local Z. The arrow shows the side the
/// ball should arrive from → exit toward (−Z). A level may have more than one goal.
/// </summary>
public sealed class GoalMarkerPrimitive : MarkerPrimitive
{
    private const float HalfWidth = 2.6f, Height = 5f, Post = 0.2f;

    public override string TypeId => "goal";
    public override string DisplayName => "Goal";
    public override string MarkerKind => "goal";

    public override IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("yaw", "Facing (°)", ParamType.Float, 0.0f, -360f, 360f),
    };

    protected override Color ProxyColor => new(1.0f, 0.3f, 0.45f);

    protected override void BuildProxy(SurfaceTool st, PrimitiveInstanceData data)
    {
        AddBox(st, new Vector3(-HalfWidth - Post, 0, -Post), new Vector3(-HalfWidth + Post, Height, Post));
        AddBox(st, new Vector3(HalfWidth - Post, 0, -Post), new Vector3(HalfWidth + Post, Height, Post));
        AddBox(st, new Vector3(-HalfWidth - Post, Height - 0.4f, -Post), new Vector3(HalfWidth + Post, Height, Post));
        AddFacingArrow(st, 1.8f, 0.02f);
    }

    protected override (Vector3 size, Vector3 centre) PickBox(PrimitiveInstanceData data)
        => (new Vector3(HalfWidth * 2 + Post * 2, Height, Post * 2), new Vector3(0, Height * 0.5f, 0));
}
