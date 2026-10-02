using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A trigger zone (Super Corgi Ball): a <c>width</c> x <c>height</c> x <c>depth</c> box standing on the
/// marker. When the ball enters it fires <c>channel</c> — every piece whose motion is "On trigger" with that
/// channel moves — and the game remembers the channel fired (the hook for a narrator reacting to the
/// player's choices). <c>once</c> = only the first entry counts. Invisible in game.
/// </summary>
public sealed class TriggerMarkerPrimitive : MarkerPrimitive
{
    public override string TypeId => "trigger";
    public override string DisplayName => "Trigger Zone";
    public override string MarkerKind => "trigger";

    public override IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("yaw",     "Facing (°)", ParamType.Float, 0.0f, -360f, 360f),
        new ParamSpec("width",   "Width",      ParamType.Float, 4.0f, 0.5f,  200f),
        new ParamSpec("depth",   "Depth",      ParamType.Float, 2.0f, 0.5f,  200f),
        new ParamSpec("height",  "Height",     ParamType.Float, 3.0f, 0.5f,  100f),
        new ParamSpec("channel", "Channel",    ParamType.Int,   1,    1f,    99f),
        new ParamSpec("once",    "Only once",  ParamType.Bool,  true),
    };

    protected override Color ProxyColor => new(1.0f, 0.9f, 0.2f);

    protected override void BuildProxy(SurfaceTool st, PrimitiveInstanceData data)
    {
        // Wireframe-ish box: the 12 edges as thin bars, so what's inside stays visible.
        float hw = GetF(data, "width", 4f) * 0.5f, hd = GetF(data, "depth", 2f) * 0.5f, h = GetF(data, "height", 3f);
        const float r = 0.05f;
        foreach (float y in new[] { 0f, h - r })
        {
            AddBox(st, new Vector3(-hw, y, -hd), new Vector3(hw, y + r, -hd + r));
            AddBox(st, new Vector3(-hw, y, hd - r), new Vector3(hw, y + r, hd));
            AddBox(st, new Vector3(-hw, y, -hd), new Vector3(-hw + r, y + r, hd));
            AddBox(st, new Vector3(hw - r, y, -hd), new Vector3(hw, y + r, hd));
        }
        foreach (float x in new[] { -hw, hw - r })
            foreach (float z in new[] { -hd, hd - r })
                AddBox(st, new Vector3(x, 0, z), new Vector3(x + r, h, z + r));
    }

    // Only a thin slab at the base is clickable, so the floor/pieces inside the zone stay pickable.
    protected override (Vector3 size, Vector3 centre) PickBox(PrimitiveInstanceData data)
        => (new Vector3(GetF(data, "width", 4f), 0.15f, GetF(data, "depth", 2f)), new Vector3(0, 0.075f, 0));

    public override IEnumerable<(string key, Variant value)> BakeMeta(PrimitiveInstanceData data)
    {
        yield return ("size", new Vector3(GetF(data, "width", 4f), GetF(data, "height", 3f), GetF(data, "depth", 2f)));
        yield return ("channel", (float)(data.Parameters.ContainsKey("channel") ? data.Parameters["channel"].AsInt32() : 1));
        bool once = !data.Parameters.ContainsKey("once") || data.Parameters["once"].AsBool();
        yield return ("once", once ? 1f : 0f); // numbers only: the .scblevel loader accepts no bools/strings
    }
}
