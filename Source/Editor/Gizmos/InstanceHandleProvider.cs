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
    public static List<IEditHandle> Build(PrimitiveInstanceData inst, IPrimitive prim, Vector3 elevationOffset,
        GridSettings grid, int selectedPathPoint = -1, int selectedHole = -1, System.Action resetSubSelection = null)
    {
        var handles = new List<IEditHandle>();
        if (prim == null) return handles;

        // World transform of the instance (basis + storey-local origin + storey elevation).
        var world = new Transform3D(inst.LocalTransform.Basis, inst.LocalTransform.Origin + elevationOffset);

        // Gameplay markers (Start, Goal, platforms …) turn with a yaw ring handle; the arm reaches past the
        // piece's footprint so the widget isn't buried inside it.
        if (prim is MarkerPrimitive)
        {
            float reach = prim.TypeId switch
            {
                "goal" => 3.4f,
                "platform" or "conveyor" => Mathf.Max(GetF(inst, "width", 4f), GetF(inst, "depth", 4f)) * 0.5f + 0.8f,
                "bumper" => GetF(inst, "radius", 0.8f) + 0.8f,
                _ => 2.2f,
            };
            handles.Add(new YawHandle(inst, world.Origin, reach));
        }

        switch (prim.TypeId)
        {
            case "path_sweep":
            {
                // Path3D-style: every point shows a small click-to-select marker; only the SELECTED point
                // unfolds its full gizmo set (X/Z mover + vertical mover + bank + remove). Adding a point
                // mid-path is a click on the overlay line (EditorContext), not a per-segment widget — keeps
                // the view uncluttered. The path is fully described by its points (identity basis), so
                // anchors are just worldOffset + localPoint.
                Vector3 off = inst.LocalTransform.Origin + elevationOffset;
                Godot.Collections.Array<Godot.Vector3> pts = PathPoints.Read(inst);
                bool pathClosed = inst.Parameters.ContainsKey("closed") && inst.Parameters["closed"].AsBool()
                                  && pts.Count >= 3;
                int sel = selectedPathPoint; // a stale index (≥ count after a delete) just shows no gizmos
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i == sel)
                    {
                        handles.Add(new PathPointHandle(inst, i, off, grid.CellSize, grid.HeightStep, vertical: false));
                        handles.Add(new PathPointHandle(inst, i, off, grid.CellSize, grid.HeightStep, vertical: true));
                        handles.Add(new PathBankHandle(inst, i, off + pts[i], PathLateral(pts, i)));
                        if (pts.Count > 2) handles.Add(new PathRemoveHandle(inst, i, off));
                    }
                    else
                    {
                        handles.Add(new PathPointMarkerHandle(i, off + pts[i], PointMarkerColor(i, pts.Count)));
                    }
                }
                // Open paths can grow past either end (drag a new point off the first/last point).
                if (!pathClosed && pts.Count >= 2)
                {
                    handles.Add(new PathExtendHandle(inst, atStart: true, off, grid.CellSize));
                    handles.Add(new PathExtendHandle(inst, atStart: false, off, grid.CellSize));
                }
                break;
            }
            case "polygon_floor":
            {
                // Same Path3D-style editing as path_sweep, but planar: every corner shows a select marker;
                // only the SELECTED corner unfolds its gizmos (an X/Z mover, plus a remove widget while
                // >3 corners remain). Adding a corner is a click on the overlay line (EditorContext). No
                // height/bank (the floor is flat) and no end-extension (the outline is a closed ring).
                Vector3 polyOff = inst.LocalTransform.Origin + elevationOffset;

                // Outer ring (ring -1): markers per corner; the selected outer corner unfolds move + remove.
                Godot.Collections.Array<Godot.Vector3> polyPts = PathPoints.Read(inst);
                for (int i = 0; i < polyPts.Count; i++)
                {
                    if (selectedHole < 0 && i == selectedPathPoint)
                    {
                        handles.Add(new PolygonPointHandle(inst, i, polyOff, grid.CellSize));
                        if (polyPts.Count > 3) handles.Add(new PolygonRemoveHandle(inst, i, polyOff));
                    }
                    else
                    {
                        handles.Add(new PathPointMarkerHandle(i, polyOff + polyPts[i], new Color(0.85f, 0.85f, 0.9f), ring: -1));
                    }
                }

                // Holes (ring h, warm markers): the selected hole corner unfolds move + corner-remove (>3),
                // and the selected hole gets a centre delete-whole-hole widget.
                System.Collections.Generic.List<System.Collections.Generic.List<Godot.Vector3>> holes = PolygonHoles.Decode(inst);
                var holeMarker = new Color(1.0f, 0.6f, 0.25f);
                for (int h = 0; h < holes.Count; h++)
                {
                    System.Collections.Generic.List<Godot.Vector3> ring = holes[h];
                    for (int i = 0; i < ring.Count; i++)
                    {
                        if (selectedHole == h && i == selectedPathPoint)
                        {
                            handles.Add(new PolygonHolePointHandle(inst, h, i, polyOff, grid.CellSize));
                            if (ring.Count > 3) handles.Add(new PolygonHoleCornerRemoveHandle(inst, h, i, polyOff));
                        }
                        else
                        {
                            handles.Add(new PathPointMarkerHandle(i, polyOff + ring[i], holeMarker, ring: h));
                        }
                    }
                    if (selectedHole == h) handles.Add(new PolygonHoleDeleteHandle(inst, h, polyOff, resetSubSelection));
                }
                break;
            }
            case "circle_plane":
            {
                float cr = GetF(inst, "radius", 2f), ct = GetF(inst, "thickness", 0.2f);
                // Radius grows from the fixed axis (origin) on the +X and +Z rim; thickness grows down from the
                // fixed top surface (y=0).
                AddFace(handles, inst, prim, world, "radius", new Vector3(cr, 0, 0), new Vector3(1, 0, 0), 0f);
                AddFace(handles, inst, prim, world, "radius", new Vector3(0, 0, cr), new Vector3(0, 0, 1), 0f);
                AddFace(handles, inst, prim, world, "thickness", new Vector3(0, -ct, 0), new Vector3(0, -1, 0), 0f);
                break;
            }
            case "half_circle":
            {
                float hr = GetF(inst, "radius", 2f), ht = GetF(inst, "thickness", 0.2f);
                // Radius on the arc (local +Z bulge) and on both diameter ends (±X); thickness grows down.
                AddFace(handles, inst, prim, world, "radius", new Vector3(0, 0, hr), new Vector3(0, 0, 1), 0f);
                AddFace(handles, inst, prim, world, "radius", new Vector3(hr, 0, 0), new Vector3(1, 0, 0), 0f);
                AddFace(handles, inst, prim, world, "radius", new Vector3(-hr, 0, 0), new Vector3(-1, 0, 0), 0f);
                AddFace(handles, inst, prim, world, "thickness", new Vector3(0, -ht, 0), new Vector3(0, -1, 0), 0f);
                break;
            }
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
                float radius = GetF(inst, "radius", 4f), rise = GetF(inst, "rise", 0f);
                float dir = arcD >= 0 ? 1f : -1f;
                float beta = Mathf.DegToRad(bank), arcR = Mathf.Abs(Mathf.DegToRad(arcD));
                // Entry cross-section: lateral runs across the path; +lateral is the outer (raised) edge.
                var lateral = new Vector3(0, Mathf.Sin(beta), dir * Mathf.Cos(beta));
                // Width grows symmetric about the centreline (origin fixed, shiftFactor 0) so the centreline
                // stays on its radius circle — a handle on each edge of the entry cross-section.
                AddFace(handles, inst, prim, world, "width", lateral * (w * 0.5f), lateral, 0f);
                AddFace(handles, inst, prim, world, "width", -lateral * (w * 0.5f), -lateral, 0f);
                // Thickness grows straight down from the fixed walkable top.
                AddFace(handles, inst, prim, world, "thickness", new Vector3(0, -t, 0), new Vector3(0, -1, 0), 0f);
                // Rise lifts the far end of the sweep (entry stays at y=0) — the helix climb.
                var farCentre = new Vector3(radius * Mathf.Sin(arcR), rise, dir * (radius * Mathf.Cos(arcR) - radius));
                AddFace(handles, inst, prim, world, "rise", farCentre + new Vector3(0, 0.3f, 0), new Vector3(0, 1, 0), 0f);
                break;
            }
            case "edge_curb":
            {
                float cw = GetF(inst, "width", 4f), cd = GetF(inst, "depth", 4f), rh = GetF(inst, "railHeight", 0.3f);
                var midH = new Vector3(0, rh * 0.5f, 0);
                AddCentered(handles, inst, prim, world, "width", new Vector3(1, 0, 0), cw * 0.5f, midH);
                AddCentered(handles, inst, prim, world, "depth", new Vector3(0, 0, 1), cd * 0.5f, midH);
                // Rail height grows up from the fixed base (y=0).
                AddFace(handles, inst, prim, world, "railHeight", new Vector3(0, rh, 0), new Vector3(0, 1, 0), 0f);
                break;
            }
            case "cylinder":
            {
                float r = GetF(inst, "radius", 1f), ch = GetF(inst, "height", 3f);
                var midH = new Vector3(0, ch * 0.5f, 0);
                // Radius grows from the fixed axis (origin), shown on the +X and +Z rim; height grows up from y=0.
                AddFace(handles, inst, prim, world, "radius", new Vector3(r, 0, 0) + midH, new Vector3(1, 0, 0), 0f);
                AddFace(handles, inst, prim, world, "radius", new Vector3(0, 0, r) + midH, new Vector3(0, 0, 1), 0f);
                AddFace(handles, inst, prim, world, "height", new Vector3(0, ch, 0), new Vector3(0, 1, 0), 0f);
                break;
            }
            case "curved_wall":
            {
                float wh = GetF(inst, "height", 1f), wt = GetF(inst, "thickness", 0.2f), warc = GetF(inst, "arc", 90f);
                float wdir = warc >= 0 ? 1f : -1f;
                var ro = new Vector3(0, 0, wdir);           // entry radial outward
                var mid = new Vector3(0, wh * 0.5f, 0);
                // Thickness grows symmetric about the centreline (both faces); height grows up from y=0.
                AddFace(handles, inst, prim, world, "thickness", ro * (wt * 0.5f) + mid, ro, 0f);
                AddFace(handles, inst, prim, world, "thickness", -ro * (wt * 0.5f) + mid, -ro, 0f);
                AddFace(handles, inst, prim, world, "height", new Vector3(0, wh, 0), new Vector3(0, 1, 0), 0f);
                break;
            }
            case "dome":
            {
                float dr = GetF(inst, "radius", 2f), dh = GetF(inst, "height", 2f);
                // Radius grows from the fixed axis on the base ring; height grows up (apex / bowl rim).
                AddFace(handles, inst, prim, world, "radius", new Vector3(dr, 0, 0), new Vector3(1, 0, 0), 0f);
                AddFace(handles, inst, prim, world, "radius", new Vector3(0, 0, dr), new Vector3(0, 0, 1), 0f);
                AddFace(handles, inst, prim, world, "height", new Vector3(0, dh, 0), new Vector3(0, 1, 0), 0f);
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

    /// <summary>Marker tint: first point green, last point red (Path3D's direction cue), the rest neutral.</summary>
    private static Color PointMarkerColor(int i, int count)
    {
        if (i == 0) return new Color(0.4f, 1.0f, 0.45f);
        if (i == count - 1) return new Color(1.0f, 0.4f, 0.35f);
        return new Color(0.85f, 0.85f, 0.9f);
    }

    /// <summary>A horizontal unit vector perpendicular to the path's local direction at point <paramref name="i"/>
    /// (from its neighbours) — where the bank handle's arm sticks out. Falls back to +Z when degenerate.</summary>
    private static Vector3 PathLateral(Godot.Collections.Array<Vector3> pts, int i)
    {
        Vector3 prev = pts[Mathf.Max(0, i - 1)], next = pts[Mathf.Min(pts.Count - 1, i + 1)];
        Vector3 tangent = next - prev;
        Vector3 lateral = tangent.Cross(Vector3.Up);
        return lateral.LengthSquared() > 1e-6f ? lateral.Normalized() : new Vector3(0, 0, 1);
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
