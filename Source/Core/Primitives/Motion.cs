using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// Optional motion for ANY geometry instance (wall, door frame, floor, curved wall …) — Super Corgi Ball's
/// sliding/rotating walls and doors. Stored as ordinary <c>m_*</c> parameters on the instance (so undo,
/// save and the inspector need nothing new); the inspector shows <see cref="Specs"/> in a Motion section for
/// every non-marker primitive.
///
/// The piece travels between its placed pose (s = 0) and an end pose (s = 1): slide by <c>m_move*</c>
/// (instance-local metres) and swing <c>m_angle</c> degrees about <c>m_axis</c> through the hinge point
/// <c>m_hinge*</c> (instance-local). Loop mode eases there-and-back every <c>m_period</c> seconds and may
/// also spin continuously (<c>m_spin</c> °/s about local Y); trigger mode moves to the end pose over
/// <c>m_period</c> seconds when its <c>m_channel</c> fires (a Trigger zone), returning after <c>m_return</c>
/// seconds (0 = stays). Moving pieces are baked out of the static chunk as <c>mover</c> markers.
/// The game mirrors <see cref="PoseAt"/> in <c>Mover.cs</c> — keep them in sync.
/// </summary>
public static class Motion
{
    public const int None = 0, Loop = 1, Triggered = 2;

    public static readonly IReadOnlyList<ParamSpec> Specs = new[]
    {
        new ParamSpec("m_mode",    "Motion",            ParamType.Int,   0,    0f, 2f, new[] { "None", "Loop", "On trigger" }),
        new ParamSpec("m_moveX",   "Slide X (local)",   ParamType.Float, 0.0f, -500f, 500f),
        new ParamSpec("m_moveY",   "Slide Y",           ParamType.Float, 0.0f, -500f, 500f),
        new ParamSpec("m_moveZ",   "Slide Z (local)",   ParamType.Float, 0.0f, -500f, 500f),
        new ParamSpec("m_axis",    "Rotate axis",       ParamType.Int,   0,    0f, 2f, new[] { "Y (vertical hinge)", "X", "Z" }),
        new ParamSpec("m_angle",   "Rotate (°)",        ParamType.Float, 0.0f, -360f, 360f),
        new ParamSpec("m_hingeX",  "Hinge X (local)",   ParamType.Float, 0.0f, -500f, 500f),
        new ParamSpec("m_hingeY",  "Hinge Y",           ParamType.Float, 0.0f, -500f, 500f),
        new ParamSpec("m_hingeZ",  "Hinge Z (local)",   ParamType.Float, 0.0f, -500f, 500f),
        new ParamSpec("m_spin",    "Spin (°/s, loop)",  ParamType.Float, 0.0f, -720f, 720f),
        new ParamSpec("m_period",  "Period / travel (s)", ParamType.Float, 4.0f, 0.1f, 600f),
        new ParamSpec("m_phase",   "Phase (0-1, loop)", ParamType.Float, 0.0f, 0f, 1f),
        new ParamSpec("m_channel", "Trigger channel",   ParamType.Int,   1,    1f, 99f),
        new ParamSpec("m_return",  "Return after (s, 0 = stay)", ParamType.Float, 0.0f, 0f, 600f),
    };

    public static int Mode(PrimitiveInstanceData d) => d.Parameters.ContainsKey("m_mode") ? d.Parameters["m_mode"].AsInt32() : None;

    public static bool IsMoving(PrimitiveInstanceData d) => Mode(d) != None;

    public static Vector3 MoveOffset(PrimitiveInstanceData d) => new(F(d, "m_moveX"), F(d, "m_moveY"), F(d, "m_moveZ"));
    public static Vector3 Hinge(PrimitiveInstanceData d) => new(F(d, "m_hingeX"), F(d, "m_hingeY"), F(d, "m_hingeZ"));

    public static Vector3 AxisVector(int axis) => axis switch { 1 => Vector3.Right, 2 => Vector3.Back, _ => Vector3.Up };

    /// <summary>Pose relative to the placed pose at travel <paramref name="s"/> (0..1), before any spin.</summary>
    public static Transform3D PoseAt(PrimitiveInstanceData d, float s)
    {
        Vector3 hinge = Hinge(d);
        var swing = new Basis(AxisVector(d.Parameters.ContainsKey("m_axis") ? d.Parameters["m_axis"].AsInt32() : 0),
                              Mathf.DegToRad(F(d, "m_angle") * s));
        // Rotate about the hinge, then slide.
        var aboutHinge = new Transform3D(swing, hinge - swing * hinge);
        return new Transform3D(Basis.Identity, MoveOffset(d) * s) * aboutHinge;
    }

    /// <summary>Values the game needs, written as <c>scb_*</c> metadata on the baked mover marker.</summary>
    public static IEnumerable<(string key, Variant value)> BakeMeta(PrimitiveInstanceData d)
    {
        yield return ("mode", (float)Mode(d));
        yield return ("move", MoveOffset(d));
        yield return ("axis", (float)(d.Parameters.ContainsKey("m_axis") ? d.Parameters["m_axis"].AsInt32() : 0));
        yield return ("angle", F(d, "m_angle"));
        yield return ("hinge", Hinge(d));
        yield return ("spin", F(d, "m_spin"));
        yield return ("period", F(d, "m_period", 4f));
        yield return ("phase", F(d, "m_phase"));
        yield return ("channel", (float)(d.Parameters.ContainsKey("m_channel") ? d.Parameters["m_channel"].AsInt32() : 1));
        yield return ("return", F(d, "m_return"));
    }

    private static float F(PrimitiveInstanceData d, string key, float def = 0f)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsSingle() : def;
}
