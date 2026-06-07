using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Primitives;

namespace LevelBuilder.Editor.Gizmos;

/// <summary>
/// Builds the resize handles for a selected instance. Knows each primitive's editable dimensions
/// and where their face handles sit (local space); the generic <see cref="AxisResizeHandle"/> does
/// the dragging. Add a case here when a new primitive type gains resizable dimensions.
/// </summary>
public static class InstanceHandleProvider
{
    public static List<IEditHandle> Build(PrimitiveInstanceData inst, IPrimitive prim, Vector3 elevationOffset)
    {
        var handles = new List<IEditHandle>();
        if (prim == null) return handles;

        // World transform of the instance (basis + storey-local origin + storey elevation).
        var world = new Transform3D(inst.LocalTransform.Basis, inst.LocalTransform.Origin + elevationOffset);

        switch (prim.TypeId)
        {
            case "floor":
            {
                float w = GetF(inst, "width", 4f), d = GetF(inst, "depth", 4f), t = GetF(inst, "thickness", 0.2f);
                AddCentered(handles, inst, prim, world, "width", new Vector3(1, 0, 0), w * 0.5f, Vector3.Zero);
                AddCentered(handles, inst, prim, world, "depth", new Vector3(0, 0, 1), d * 0.5f, Vector3.Zero);
                // Thickness from both faces: bottom handle grows down (top fixed), top handle grows up (bottom fixed).
                AddFace(handles, inst, prim, world, "thickness", new Vector3(0, -t, 0), new Vector3(0, -1, 0), 0f);
                AddFace(handles, inst, prim, world, "thickness", new Vector3(0, 0, 0), new Vector3(0, 1, 0), 1f);
                break;
            }
            case "wall":
            {
                float l = GetF(inst, "length", 1f), h = GetF(inst, "height", 3f), t = GetF(inst, "thickness", 0.2f);
                AddLength(handles, inst, prim, world, l, h);
                AddCentered(handles, inst, prim, world, "thickness", new Vector3(0, 0, 1), t * 0.5f, new Vector3(0, h * 0.5f, 0));
                // Height grows upward from the fixed base (y=0) — no origin shift.
                AddFace(handles, inst, prim, world, "height", new Vector3(0, h, 0), new Vector3(0, 1, 0), 0f);
                break;
            }
            case "ramp":
            {
                float l = GetF(inst, "length", 3f), r = GetF(inst, "rise", 3f), w = GetF(inst, "width", 1.2f);
                var midH = new Vector3(0, r * 0.5f, 0);
                AddCentered(handles, inst, prim, world, "length", new Vector3(1, 0, 0), l * 0.5f, midH);
                AddCentered(handles, inst, prim, world, "width", new Vector3(0, 0, 1), w * 0.5f, midH);
                // Rise grows up from the fixed base (y=0), handled at the high (back) end.
                AddFace(handles, inst, prim, world, "rise", new Vector3(l * 0.5f, r, 0), new Vector3(0, 1, 0), 0f);
                break;
            }
            case "stairs":
            {
                float run = GetF(inst, "run", 3f), rise = GetF(inst, "totalRise", 3f), w = GetF(inst, "width", 1.2f);
                var midH = new Vector3(0, rise * 0.5f, 0);
                AddCentered(handles, inst, prim, world, "run", new Vector3(1, 0, 0), run * 0.5f, midH);
                AddCentered(handles, inst, prim, world, "width", new Vector3(0, 0, 1), w * 0.5f, midH);
                // Total rise grows up from the fixed base (y=0), handled at the high (back) end. (Step count not gizmo-editable.)
                AddFace(handles, inst, prim, world, "totalRise", new Vector3(run * 0.5f, rise, 0), new Vector3(0, 1, 0), 0f);
                break;
            }
            case "ramp_plane":
            {
                float l = GetF(inst, "length", 3f), r = GetF(inst, "rise", 3f), w = GetF(inst, "width", 1.2f), t = GetF(inst, "thickness", 0.2f);
                var midH = new Vector3(0, r * 0.5f, 0);
                AddCentered(handles, inst, prim, world, "length", new Vector3(1, 0, 0), l * 0.5f, midH);
                AddCentered(handles, inst, prim, world, "width", new Vector3(0, 0, 1), w * 0.5f, midH);
                AddFace(handles, inst, prim, world, "rise", new Vector3(l * 0.5f, r, 0), new Vector3(0, 1, 0), 0f);
                // Thickness grows along the slab's downward normal from the fixed top surface.
                var down = new Vector3(r, -l, 0) / Mathf.Sqrt(l * l + r * r);
                AddFace(handles, inst, prim, world, "thickness", midH + down * t, down, 0f);
                break;
            }
            case "banked_curve":
            {
                float w = GetF(inst, "width", 2f), t = GetF(inst, "thickness", 0.2f);
                float bank = GetF(inst, "bank", 0f), arcD = GetF(inst, "arc", 90f);
                float dir = arcD >= 0 ? 1f : -1f;
                float beta = Mathf.DegToRad(bank);
                // Entry cross-section: lateral runs across the path; +lateral is the outer (raised) edge.
                var lateral = new Vector3(0, Mathf.Sin(beta), dir * Mathf.Cos(beta));
                // Width grows symmetric about the centreline (origin fixed, shiftFactor 0) so the centreline
                // stays on its radius circle — a handle on each edge of the entry cross-section.
                AddFace(handles, inst, prim, world, "width", lateral * (w * 0.5f), lateral, 0f);
                AddFace(handles, inst, prim, world, "width", -lateral * (w * 0.5f), -lateral, 0f);
                // Thickness grows straight down from the fixed walkable top.
                AddFace(handles, inst, prim, world, "thickness", new Vector3(0, -t, 0), new Vector3(0, -1, 0), 0f);
                break;
            }
            case "half_pipe":
            {
                // Rotational params (curve, arc, bank) and radius have no clean axis drag → inspector only.
                // The linear ones do: rise lifts the far end, and length stretches it (only while straight,
                // since once curved the far end leaves local +X). Both keep the entry (f=0) end fixed.
                float length = GetF(inst, "length", 4f), rise = GetF(inst, "rise", 0f), curve = GetF(inst, "curve", 0f);
                float curveRad = Mathf.DegToRad(curve);
                bool straight = Mathf.Abs(curveRad) < 1e-4f;
                Vector3 farH;
                if (straight)
                    farH = new Vector3(length, 0, 0);
                else
                {
                    float sgn = curve >= 0 ? 1f : -1f, rh = length / Mathf.Abs(curveRad), th = Mathf.Abs(curveRad);
                    farH = new Vector3(rh * Mathf.Sin(th), 0, sgn * (rh * Mathf.Cos(th) - rh));
                }
                var farP = new Vector3(farH.X, rise, farH.Z);
                AddFace(handles, inst, prim, world, "rise", farP + new Vector3(0, 0.3f, 0), new Vector3(0, 1, 0), 0f);
                if (straight)
                    AddFace(handles, inst, prim, world, "length", farP, new Vector3(1, 0, 0), 0f);
                break;
            }
            case "stair_plane":
            {
                float run = GetF(inst, "run", 3f), rise = GetF(inst, "totalRise", 3f), w = GetF(inst, "width", 1.2f), t = GetF(inst, "thickness", 0.1f);
                var midH = new Vector3(0, rise * 0.5f, 0);
                AddCentered(handles, inst, prim, world, "run", new Vector3(1, 0, 0), run * 0.5f, midH);
                AddCentered(handles, inst, prim, world, "width", new Vector3(0, 0, 1), w * 0.5f, midH);
                AddFace(handles, inst, prim, world, "totalRise", new Vector3(run * 0.5f, rise, 0), new Vector3(0, 1, 0), 0f);
                // Thickness grows into the underside (the +X,−Y miter direction) from the fixed top.
                var into = new Vector3(1, -1, 0).Normalized();
                AddFace(handles, inst, prim, world, "thickness", midH + into * t, into, 0f);
                break;
            }
        }
        return handles;
    }

    /// <summary>
    /// A centered dimension gets a handle on each face. The handle sits at
    /// <c>±axis·halfExtent + perpOffset</c>: only the on-axis half flips between the two faces, the
    /// perpendicular placement (e.g. a wall handle's mid-height) is the same on both. Dragging either
    /// keeps the far face fixed (shiftFactor 0.5).
    /// </summary>
    private static void AddCentered(List<IEditHandle> handles, PrimitiveInstanceData inst, IPrimitive prim,
        Transform3D world, string param, Vector3 localAxis, float halfExtent, Vector3 perpOffset)
    {
        AddFace(handles, inst, prim, world, param, localAxis * halfExtent + perpOffset, localAxis, 0.5f);
        AddFace(handles, inst, prim, world, param, -localAxis * halfExtent + perpOffset, -localAxis, 0.5f);
    }

    /// <summary>
    /// Wall length: a centered handle on each end, but each also carries the wall's openings so they
    /// hold their world position. Dragging the +X end leaves the u=0 (−X) end — which offsets are
    /// measured from — fixed (openComp 0); dragging the −X end moves it the full growth (openComp 1).
    /// </summary>
    private static void AddLength(List<IEditHandle> handles, PrimitiveInstanceData inst, IPrimitive prim,
        Transform3D world, float l, float h)
    {
        (float min, float max) = Range(prim, "length");
        OpeningData[] openings = ToArray(inst.Openings);
        var perp = new Vector3(0, h * 0.5f, 0);
        AddResize(handles, inst, "length", min, max, world, new Vector3(l * 0.5f, 0, 0) + perp, new Vector3(1, 0, 0), 0.5f, openings, 0f);
        AddResize(handles, inst, "length", min, max, world, new Vector3(-l * 0.5f, 0, 0) + perp, new Vector3(-1, 0, 0), 0.5f, openings, 1f);
    }

    /// <summary>One handle: anchor + outward axis (local), with the origin-shift factor that fixes a face.</summary>
    private static void AddFace(List<IEditHandle> handles, PrimitiveInstanceData inst, IPrimitive prim,
        Transform3D world, string param, Vector3 localAnchor, Vector3 localAxis, float shiftFactor)
    {
        (float min, float max) = Range(prim, param);
        AddResize(handles, inst, param, min, max, world, localAnchor, localAxis, shiftFactor, null, 0f);
    }

    private static void AddResize(List<IEditHandle> handles, PrimitiveInstanceData inst, string param, float min, float max,
        Transform3D world, Vector3 localAnchor, Vector3 localAxis, float shiftFactor,
        OpeningData[] openings, float openComp)
    {
        Vector3 anchor = world * localAnchor;
        Vector3 axis = (world.Basis * localAxis).Normalized();
        handles.Add(new AxisResizeHandle(inst, param, min, max, anchor, axis, shiftFactor, openings, openComp));
    }

    private static OpeningData[] ToArray(Godot.Collections.Array<OpeningData> openings)
    {
        var arr = new OpeningData[openings.Count];
        for (int i = 0; i < openings.Count; i++) arr[i] = openings[i];
        return arr;
    }

    private static (float, float) Range(IPrimitive prim, string key)
    {
        foreach (ParamSpec spec in prim.Parameters)
            if (spec.Key == key) return (spec.Min, spec.Max);
        return (0.01f, 1000f);
    }

    private static float GetF(PrimitiveInstanceData d, string key, float def)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsSingle() : def;
}
