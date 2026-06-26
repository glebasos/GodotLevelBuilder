using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Geometry;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A flat floor slab whose outline is an arbitrary polygon (not the axis-aligned width×depth box of
/// <see cref="FloorPrimitive"/>) — for angled / rhombus / freeform playfields. The outline is a list of
/// control points stored in <c>Parameters["points"]</c> (local space, relative to the instance origin,
/// Y ignored — the slab is planar), treated as a CLOSED ring. The top face sits at local y = 0 and the
/// slab extends down by <c>thickness</c>.
///
/// The top/bottom caps are triangulated with <see cref="Geometry2D.TriangulatePolygon"/> (which the path
/// tool already uses for its end caps); each triangle is oriented by its own geometric normal toward
/// ±Y, so the polygon's winding doesn't matter. When that returns nothing — the outline is momentarily
/// SELF-INTERSECTING, which happens constantly while rubber-banding the live preview — the cap falls back
/// to a centroid fan (exact for a convex polygon, and the preview never blanks out). The sides are one
/// vertical quad per edge, oriented outward from the polygon centroid. Surfaces: 0 = Top, 1 = Bottom, 2 = Edge.
///
/// Holes live in <c>Parameters["holes"]/["holeSizes"]</c> (see <see cref="PolygonHoles"/>). Rather than
/// triangulate a polygon-with-holes directly (CLAUDE.md gotcha #1), each hole is BRIDGED into the outline
/// — a zero-width seam stitches the rings into one simple polygon that
/// <see cref="Geometry2D.TriangulatePolygon"/> handles — and each hole gets inward-facing side walls, so
/// the trimesh collision has real voids (the ball falls through). Holes are bridged SEQUENTIALLY,
/// right-to-left (max-X descending); per hole TWO bridge variants are tried (vertex / edge — they pinch on
/// complementary grid-snap configs) and a hole is accepted only if the REAL <see cref="Geometry2D.
/// TriangulatePolygon"/> succeeds on the result — so an out-of-bounds, overlapping, or otherwise un-bridgeable
/// ring is silently SKIPPED while the others still render, and the combined polygon is always triangulable
/// (one bad hole can never blank the rest). Walls are emitted only for the holes that bridged, so the cap and
/// the walls always agree. The bridging + try-both strategy was validated in a standalone harness.
/// </summary>
public sealed class PolygonFloorPrimitive : IPrimitive
{
    public string TypeId => "polygon_floor";
    public string DisplayName => "Polygon Floor";
    public string Category => "Structure";

    public IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("thickness", "Thickness", ParamType.Float, 0.2f, 0.01f, 100f),
        // Auto-rail following the outer outline. None = bare slab; Rail = solid curb/lip; Elevated Rail =
        // posts + top beam (fence); Bank = an angled wedge that funnels the ball back to centre. Enum values
        // are APPEND-ONLY so stored values stay stable across round-trips.
        new ParamSpec("rail",       "Rail",        ParamType.Int,   0, 0f, 3f, new[] { "None", "Rail", "Elevated Rail", "Bank" }),
        // Independent rim style for the HOLE perimeters (same four styles), so holes can carry a different
        // rail than the outer outline — or none at all. Append-only enum, same as "rail".
        new ParamSpec("holeRail",   "Hole Rail",   ParamType.Int,   0, 0f, 3f, new[] { "None", "Rail", "Elevated Rail", "Bank" }),
        new ParamSpec("railHeight", "Rail Height", ParamType.Float, 0.4f, 0.02f, 50f),
        new ParamSpec("railWidth",  "Rail Width",  ParamType.Float, 0.2f, 0.02f, 50f),
    };

    // "Rail" is a TRAILING, CONDITIONAL slot: surface 3 is emitted only when rail != None (like the dome's
    // Side / wall's Reveal). The baker/resolver map slot index → surface positionally, so an absent surface 3
    // just leaves the Rail slot unused — index→slot stays stable for Top/Bottom/Edge.
    public IReadOnlyList<string> MaterialSlots { get; } = new[] { "Top", "Bottom", "Edge", "Rail" };

    public ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx)
    {
        var mesh = new ArrayMesh();
        List<Vector3> pts = ReadPoints(data, "points");
        if (pts.Count < 3) return mesh; // need a triangle's worth of outline

        float t = GetF(data, "thickness", 0.2f);

        // 2D outline (XZ) for triangulation; centroid for the fan fallback + outward side-wall normals.
        var outer2d = new Vector2[pts.Count];
        Vector3 centre = Vector3.Zero;
        for (int i = 0; i < pts.Count; i++)
        {
            outer2d[i] = new Vector2(pts[i].X, pts[i].Z);
            centre += pts[i];
        }
        centre /= pts.Count;
        var centroid2d = new Vector2(centre.X, centre.Z);

        // Holes: bridge each into the outline so a single simple polygon triangulates the holed cap. BridgeMany
        // accepts a hole ONLY if the real TriangulatePolygon succeeds on the result, so the combined polygon is
        // always triangulable (a hole that can't bridge cleanly is skipped, never corrupting the others) and the
        // walls are emitted for exactly the bridged set — cap and walls always agree.
        List<List<Vector3>> holes = PolygonHoles.Decode(data);
        Vector2[] capVerts = outer2d;
        int[] capTris = null;
        List<List<Vector3>> bridged = null;
        if (holes.Count > 0)
        {
            (Vector2[] combined, int[] ctris, List<List<Vector3>> br) = BridgeMany(outer2d, holes);
            if (br.Count > 0 && ctris != null && ctris.Length >= 3) { capVerts = combined; capTris = ctris; bridged = br; }
        }
        if (bridged == null) capTris = Geometry2D.TriangulatePolygon(outer2d); // solid; null while non-simple → fan

        // Surface 0: Top cap (y = 0, faces +Y).  Surface 1: Bottom cap (y = -t, faces -Y).
        SurfaceTool top = Begin();
        EmitCap(top, capVerts, capTris, 0, Vector3.Up, centroid2d);
        Commit(top, mesh);

        SurfaceTool bottom = Begin();
        EmitCap(bottom, capVerts, capTris, -t, Vector3.Down, centroid2d);
        Commit(bottom, mesh);

        // Surface 2: Edge walls — one vertical quad per outline edge (faced outward, away from the centroid),
        // plus each bridged hole's walls (faced inward, toward the hole centroid).
        SurfaceTool edge = Begin();
        AddWalls(edge, pts, t, centre, outward: true);
        if (bridged != null)
            foreach (List<Vector3> ring in bridged) AddWalls(edge, ring, t, Centroid(ring), outward: false);
        Commit(edge, mesh);

        // Surface 3: Rail — perimeter rims, built only when a style is selected, into a strictly-last surface
        // so the positional slot mapping for Top/Bottom/Edge is undisturbed. The OUTER outline and the HOLE
        // perimeters carry INDEPENDENT styles ("rail" vs "holeRail") but share this single surface / "Rail"
        // material slot: surface 3 is emitted whenever EITHER is non-None, so the slot stays exactly one
        // trailing-conditional surface (a second conditional surface would shift the positional mapping when
        // the outer rail is off but a hole rail is on). The outer outline insets INWARD (toward its centroid);
        // each hole insets OUTWARD into the solid (away from the hole centroid, negative width) so its rim sits
        // on the slab around the void with the lip facing in. Holes only get rims for the rings that bridged.
        int railStyle = GetI(data, "rail", 0);
        int holeRailStyle = GetI(data, "holeRail", 0);
        if ((railStyle != 0 || holeRailStyle != 0) && pts.Count >= 3)
        {
            float rh = GetF(data, "railHeight", 0.4f);
            float rw = GetF(data, "railWidth", 0.2f);
            SurfaceTool rail = Begin();
            bool any = false;

            if (railStyle != 0)
            {
                float w = Mathf.Min(rw, 0.45f * MinEdge(outer2d)); // keep the inward inset from inverting on small polys
                Vector2[] inset = InsetRing(outer2d, centroid2d, w);
                if (BuildRail(rail, outer2d, inset, railStyle, rh, w)) any = true;
            }

            if (holeRailStyle != 0 && bridged != null)
            {
                foreach (List<Vector3> ring in bridged)
                {
                    var hole2d = new Vector2[ring.Count];
                    for (int i = 0; i < ring.Count; i++) hole2d[i] = new Vector2(ring[i].X, ring[i].Z);
                    Vector2 hcent = Centroid2d(hole2d);
                    float w = Mathf.Min(rw, 0.45f * MinEdge(hole2d));
                    Vector2[] inset = InsetRing(hole2d, hcent, -w); // outward into the solid (away from hole centre)
                    if (BuildRail(rail, hole2d, inset, holeRailStyle, rh, w)) any = true;
                }
            }

            if (any) Commit(rail, mesh);
        }

        return mesh;
    }

    /// <summary>
    /// Bridges every hole into the outline, processed right-to-left (max-X descending). Per hole it TRIES BOTH
    /// bridge variants — vertex-bridge then edge-bridge — and accepts the first whose <see cref="Geometry2D.
    /// TriangulatePolygon"/> actually succeeds; if neither does (out-of-bounds / overlapping / unresolvable
    /// degeneracy) the hole is skipped. The two variants pinch on complementary configurations (vertex on
    /// vertically-stacked / different-size holes, edge on same-band rows), so trying both clears the grid-snap
    /// degeneracies that a single variant — passing a simple/area check but failing the real triangulator —
    /// could not. Returns the combined polygon, ITS triangulation (reused by the caller), and the rings that
    /// bridged (so walls are emitted for exactly those). The strategy was validated in a standalone harness.
    /// </summary>
    private static (Vector2[] combined, int[] tris, List<List<Vector3>> bridged) BridgeMany(
        Vector2[] outer, List<List<Vector3>> holes)
    {
        var order = new List<List<Vector3>>(holes);
        order.Sort((a, b) => MaxX(b).CompareTo(MaxX(a)));
        var combined = new List<Vector2>(EnsureWinding(outer, ccw: true));
        int[] tris = Geometry2D.TriangulatePolygon(combined.ToArray());
        var bridged = new List<List<Vector3>>();

        foreach (List<Vector3> ring in order)
        {
            if (ring.Count < 3) continue;
            var hole2d = new Vector2[ring.Count];
            for (int i = 0; i < ring.Count; i++) hole2d[i] = new Vector2(ring[i].X, ring[i].Z);

            foreach (bool edge in new[] { false, true }) // vertex-bridge first, then edge-bridge
            {
                List<Vector2> cand = edge ? BridgeEdge(combined.ToArray(), hole2d) : BridgeVertex(combined.ToArray(), hole2d);
                if (cand == null) continue;
                int[] ct = Geometry2D.TriangulatePolygon(cand.ToArray());
                if (ct != null && ct.Length >= 3) { combined = cand; tris = ct; bridged.Add(ring); break; }
            }
        }
        return (combined.ToArray(), tris, bridged);
    }

    /// <summary>The horizontal +X ray from <paramref name="m"/>: the first outline edge it hits, as an edge
    /// index and the hit point. Returns e = -1 if none (the hole isn't inside).</summary>
    private static (int e, Vector2 hit) FirstHit(Vector2[] outer, Vector2 m)
    {
        int no = outer.Length;
        float bestX = float.MaxValue; int e = -1; Vector2 hit = default;
        for (int i = 0; i < no; i++)
        {
            Vector2 a = outer[i], b = outer[(i + 1) % no];
            if ((a.Y > m.Y) == (b.Y > m.Y)) continue;             // edge doesn't straddle the ray
            float tt = (m.Y - a.Y) / (b.Y - a.Y);
            float x = a.X + tt * (b.X - a.X);
            if (x >= m.X - 1e-6f && x < bestX) { bestX = x; e = i; hit = new Vector2(x, m.Y); }
        }
        return (e, hit);
    }

    private static float MaxX(List<Vector3> ring)
    {
        float m = float.MinValue;
        foreach (Vector3 p in ring) if (p.X > m) m = p.X;
        return m;
    }

    /// <summary>Emits a vertical quad per ring edge. <paramref name="outward"/> faces them away from
    /// <paramref name="reference"/> (the outline, seen from outside); else toward it (a hole, seen from
    /// inside the void).</summary>
    private static void AddWalls(SurfaceTool edge, List<Vector3> ring, float t, Vector3 reference, bool outward)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 a = ring[i], b = ring[(i + 1) % ring.Count];
            var topA = new Vector3(a.X, 0, a.Z);
            var topB = new Vector3(b.X, 0, b.Z);
            var botB = new Vector3(b.X, -t, b.Z);
            var botA = new Vector3(a.X, -t, a.Z);
            Vector3 mid = (topA + topB) * 0.5f;
            Vector3 face = outward ? mid - reference : reference - mid; face.Y = 0;
            if (face.LengthSquared() < 1e-9f) face = Vector3.Right;
            MeshBuilder.AddQuadFacing(edge, topA, topB, botB, botA, face);
        }
    }

    private static Vector3 Centroid(List<Vector3> ring)
    {
        Vector3 c = Vector3.Zero;
        foreach (Vector3 p in ring) c += p;
        return c / ring.Count;
    }

    private static Vector2 Centroid2d(Vector2[] ring)
    {
        Vector2 c = Vector2.Zero;
        foreach (Vector2 p in ring) c += p;
        return c / ring.Length;
    }

    // --- Auto-rail. A rim swept along the outline (in XZ), sitting ON TOP of the slab (the slab top is local
    // y=0, so the rail rises 0→h). The outline is offset inward by the rail width into a mitered INSET RING;
    // each style emits a per-edge cross-section between the outline and the inset. Winding is delegated to
    // MeshBuilder.AddQuadFacing (each face just states which way it should look), so no hand-tracked CCW.

    /// <summary>Dispatches the rail style. Returns false (nothing emitted) for an unknown style so the caller
    /// skips committing an empty surface. <paramref name="w"/> is the rail width (inset / post-beam size).</summary>
    private static bool BuildRail(SurfaceTool rail, Vector2[] outer, Vector2[] inset, int style, float h, float w)
    {
        switch (style)
        {
            case 1: BuildCurb(rail, outer, inset, h); return true;     // solid curb/lip
            case 2: BuildFence(rail, outer, h, w); return true;        // posts + top beam
            case 3: BuildBank(rail, outer, inset, h); return true;     // angled wedge
            default: return false;
        }
    }

    /// <summary>Solid curb / lip: an outer vertical wall (at the outline), an inner vertical wall (at the
    /// inset), and a flat top band joining them. No underside — it sits flush on the slab top.</summary>
    private static void BuildCurb(SurfaceTool rail, Vector2[] outer, Vector2[] inset, float h)
    {
        int n = outer.Length;
        for (int i = 0; i < n; i++)
        {
            int m = (i + 1) % n;
            Vector2 a = outer[i], b = outer[m], ai = inset[i], bi = inset[m];
            Vector3 Lo(Vector2 p) => new(p.X, 0, p.Y);
            Vector3 Hi(Vector2 p) => new(p.X, h, p.Y);

            // Outward horizontal: outline edge midpoint minus inset edge midpoint (the inset is inward).
            Vector3 outward = Lo(a) + Lo(b) - Lo(ai) - Lo(bi); outward.Y = 0;
            if (outward.LengthSquared() < 1e-9f) outward = Vector3.Right;

            MeshBuilder.AddQuadFacing(rail, Lo(a),  Lo(b),  Hi(b),  Hi(a),  outward);   // outer wall (faces out)
            MeshBuilder.AddQuadFacing(rail, Lo(ai), Lo(bi), Hi(bi), Hi(ai), -outward);  // inner wall (faces in)
            MeshBuilder.AddQuadFacing(rail, Hi(a),  Hi(b),  Hi(bi), Hi(ai), Vector3.Up);// top band (faces up)
        }
    }

    /// <summary>Angled bank: a wedge per edge — a vertical outer wall at the outline (the high lip) and an
    /// inward slope from that lip's top down to the slab at the inset, so the perimeter funnels the ball back
    /// toward the centre. Underside sits flush on the slab. Adjacent wedges share their outer-top and
    /// inset-floor vertices (mitered inset), so the join is watertight without end caps.</summary>
    private static void BuildBank(SurfaceTool rail, Vector2[] outer, Vector2[] inset, float h)
    {
        int n = outer.Length;
        for (int i = 0; i < n; i++)
        {
            int m = (i + 1) % n;
            Vector2 a = outer[i], b = outer[m], ai = inset[i], bi = inset[m];
            Vector3 Lo(Vector2 p) => new(p.X, 0, p.Y);
            Vector3 Hi(Vector2 p) => new(p.X, h, p.Y);

            Vector3 outward = Lo(a) + Lo(b) - Lo(ai) - Lo(bi); outward.Y = 0;
            if (outward.LengthSquared() < 1e-9f) outward = Vector3.Right;

            MeshBuilder.AddQuadFacing(rail, Lo(a), Lo(b), Hi(b), Hi(a), outward);          // vertical outer lip
            MeshBuilder.AddQuadFacing(rail, Hi(a), Hi(b), Lo(bi), Lo(ai), Vector3.Up);     // inward slope (faces up+in)
        }
    }

    /// <summary>Elevated rail (fence): square posts at every outline vertex and at ~2.5 m intervals along
    /// each edge, plus a top beam running the outline at the rail height. Independent of the inset ring —
    /// posts/beam are oriented boxes centred on the outline. Post + beam cross-section = <paramref name="w"/>.</summary>
    private static void BuildFence(SurfaceTool rail, Vector2[] outer, float h, float w)
    {
        int n = outer.Length;
        float half = w * 0.5f;
        const float spacing = 2.5f;

        void Post(Vector2 p)
            => AddOrientedBox(rail, new Vector3(p.X, h * 0.5f, p.Y),
                              new Vector3(half, 0, 0), new Vector3(0, h * 0.5f, 0), new Vector3(0, 0, half));

        for (int i = 0; i < n; i++) Post(outer[i]);  // a post at every corner

        for (int i = 0; i < n; i++)
        {
            Vector2 a = outer[i], b = outer[(i + 1) % n];
            float len = a.DistanceTo(b);
            if (len < 1e-4f) continue;

            int segs = Mathf.Max(1, Mathf.RoundToInt(len / spacing));
            for (int s = 1; s < segs; s++) Post(a.Lerp(b, (float)s / segs)); // interior posts

            // Top beam along the edge: top flush at y=h, cross-section w×w, oriented along the edge.
            Vector2 dir = (b - a).Normalized();
            Vector2 perp = new(-dir.Y, dir.X);
            Vector2 mid = (a + b) * 0.5f;
            AddOrientedBox(rail, new Vector3(mid.X, h - half, mid.Y),
                           new Vector3(dir.X, 0, dir.Y) * (len * 0.5f),
                           new Vector3(0, half, 0),
                           new Vector3(perp.X, 0, perp.Y) * half);
        }
    }

    /// <summary>Adds a box from a centre and three half-extent axis vectors (any orientation). Each of the 6
    /// faces is emitted via <see cref="MeshBuilder.AddQuadFacing"/> toward its outward axis, so winding is
    /// auto-resolved.</summary>
    private static void AddOrientedBox(SurfaceTool st, Vector3 c, Vector3 ax, Vector3 ay, Vector3 az)
    {
        Vector3 P(int sx, int sy, int sz) => c + ax * sx + ay * sy + az * sz;
        MeshBuilder.AddQuadFacing(st, P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), ax);
        MeshBuilder.AddQuadFacing(st, P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), -ax);
        MeshBuilder.AddQuadFacing(st, P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), ay);
        MeshBuilder.AddQuadFacing(st, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), -ay);
        MeshBuilder.AddQuadFacing(st, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), az);
        MeshBuilder.AddQuadFacing(st, P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), -az);
    }

    /// <summary>Offsets the outline inward by <paramref name="d"/> into a mitered inset ring: each edge's line
    /// is moved inward (toward <paramref name="centroid"/>) by d and consecutive lines intersected, so corners
    /// miter cleanly at any angle. Parallel/degenerate corners fall back to a straight inward vertex offset.
    /// Inherits the existing centroid-inward convention's convex / gentle-concave limit (a strongly concave
    /// corner could mis-orient) — fine for the angled playfields this serves; signed-area winding is the
    /// upgrade if robust concave is ever needed.</summary>
    private static Vector2[] InsetRing(Vector2[] poly, Vector2 centroid, float d)
    {
        int n = poly.Length;
        var op = new Vector2[n];   // a point on each inward-offset edge line
        var od = new Vector2[n];   // its direction
        for (int i = 0; i < n; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % n];
            Vector2 dir = b - a;
            if (dir.LengthSquared() < 1e-12f) { op[i] = a; od[i] = new Vector2(1, 0); continue; }
            dir = dir.Normalized();
            var nrm = new Vector2(-dir.Y, dir.X);
            Vector2 mid = (a + b) * 0.5f;
            if (nrm.Dot(centroid - mid) < 0) nrm = -nrm;  // orient inward (toward centroid)
            op[i] = a + nrm * d; od[i] = dir;
        }

        var res = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            int p = (i - 1 + n) % n;
            if (LineIntersect(op[p], od[p], op[i], od[i], out Vector2 x)) { res[i] = x; continue; }
            Vector2 toC = centroid - poly[i];   // parallel edges: offset the vertex straight inward
            res[i] = poly[i] + (toC.LengthSquared() > 1e-9f ? toC.Normalized() * d : Vector2.Zero);
        }
        return res;
    }

    /// <summary>Intersects two infinite lines (point + direction). False when near-parallel.</summary>
    private static bool LineIntersect(Vector2 p0, Vector2 d0, Vector2 p1, Vector2 d1, out Vector2 x)
    {
        float denom = d0.X * d1.Y - d0.Y * d1.X;
        if (Mathf.Abs(denom) < 1e-9f) { x = default; return false; }
        Vector2 diff = p1 - p0;
        float tt = (diff.X * d1.Y - diff.Y * d1.X) / denom;
        x = p0 + d0 * tt;
        return true;
    }

    private static float MinEdge(Vector2[] poly)
    {
        float min = float.MaxValue;
        for (int i = 0; i < poly.Length; i++)
        {
            float len = poly[i].DistanceTo(poly[(i + 1) % poly.Length]);
            if (len > 1e-4f && len < min) min = len;
        }
        return min == float.MaxValue ? 1f : min;
    }

    public Shape3D[] BuildCollision(PrimitiveInstanceData data, BuildContext ctx)
    {
        ArrayMesh mesh = BuildMesh(data, ctx);
        if (mesh.GetSurfaceCount() == 0) return System.Array.Empty<Shape3D>();
        return new Shape3D[] { mesh.CreateTrimeshShape() };
    }

    /// <summary>
    /// Emits one flat cap at height <paramref name="y"/> facing <paramref name="face"/>. Uses the
    /// <see cref="Geometry2D.TriangulatePolygon"/> result when it's valid (any simple polygon); otherwise
    /// falls back to a fan from the centroid — exact for a convex polygon and, critically, keeps the live
    /// preview visible while the in-progress outline is momentarily self-intersecting (triangulation
    /// returns nothing then). Each triangle is oriented by its own normal toward <paramref name="face"/>.
    /// </summary>
    private static void EmitCap(SurfaceTool st, Vector2[] poly2d, int[] tris, float y, Vector3 face, Vector2 centroid)
    {
        if (tris != null && tris.Length >= 3)
        {
            for (int k = 0; k + 2 < tris.Length; k += 3)
                MeshBuilder.AddTriFacing(st,
                    new Vector3(poly2d[tris[k]].X, y, poly2d[tris[k]].Y),
                    new Vector3(poly2d[tris[k + 1]].X, y, poly2d[tris[k + 1]].Y),
                    new Vector3(poly2d[tris[k + 2]].X, y, poly2d[tris[k + 2]].Y),
                    face);
            return;
        }
        var c = new Vector3(centroid.X, y, centroid.Y);
        int n = poly2d.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = poly2d[i], b = poly2d[(i + 1) % n];
            MeshBuilder.AddTriFacing(st, c, new Vector3(a.X, y, a.Y), new Vector3(b.X, y, b.Y), face);
        }
    }

    /// <summary>Reads a ring (the outline "points" or the "hole") from a parameter, dropping near-coincident
    /// consecutive points and a closing duplicate (last == first) so the implicit ring isn't double-counted.
    /// Untyped element read (AsGodotArray + AsVector3) dodges the typed-array silent-empty trap (DATA_MODEL.md).</summary>
    private static List<Vector3> ReadPoints(PrimitiveInstanceData data, string key)
    {
        var pts = new List<Vector3>();
        if (!data.Parameters.ContainsKey(key)) return pts;

        Godot.Collections.Array raw = data.Parameters[key].AsGodotArray();
        for (int i = 0; i < raw.Count; i++)
        {
            Vector3 p = raw[i].AsVector3();
            if (pts.Count > 0 && p.DistanceTo(pts[^1]) <= 1e-3f) continue;
            pts.Add(p);
        }
        if (pts.Count >= 2 && pts[0].DistanceTo(pts[^1]) <= 1e-3f) pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    // --- Hole bridging. Stitches a hole ring into the outline through a zero-width seam so the result is one
    // simple polygon TriangulatePolygon can handle (CLAUDE.md gotcha #1: don't triangulate a hole directly).
    // Two complementary variants (BridgeMany tries both per hole, real-triangulator-gated). Both force outer
    // CCW / hole CW and ray the hole's max-X vertex +X to the first outline edge. Validated in a harness.

    /// <summary>Bridge to the outline VERTEX nearest the ray hit (visible-vertex refinement). Best when the
    /// ray lands mid-edge; pinches when several holes pick the same vertex (handled by trying the edge variant).</summary>
    private static List<Vector2> BridgeVertex(Vector2[] outerIn, Vector2[] holeIn)
    {
        Vector2[] outer = EnsureWinding(outerIn, ccw: true);
        Vector2[] hole = EnsureWinding(holeIn, ccw: false);
        int no = outer.Length, nh = hole.Length;
        int mi = MaxXIndex(hole);
        Vector2 m = hole[mi];

        (int e, Vector2 inter) = FirstHit(outer, m);
        if (e < 0) return null;

        int pi = outer[e].X > outer[(e + 1) % no].X ? e : (e + 1) % no;
        Vector2 p = outer[pi];

        // Refine to the visible outline vertex: among vertices inside triangle (m, inter, p), pick the one at
        // the smallest angle to the +X ray (max cos), ties broken by nearest — otherwise m→p would be blocked.
        float bestCos = -2f, bestDist = float.MaxValue;
        for (int i = 0; i < no; i++)
        {
            if (i == pi) continue;
            Vector2 r = outer[i];
            if (!PointInTri(r, m, inter, p)) continue;
            Vector2 d = r - m; float len = d.Length();
            if (len < 1e-9f) continue;
            float cos = d.X / len;
            if (cos > bestCos + 1e-7f || (Mathf.Abs(cos - bestCos) <= 1e-7f && len < bestDist))
            { bestCos = cos; bestDist = len; pi = i; p = r; }
        }

        var res = new List<Vector2>();
        for (int k = 0; k <= pi; k++) res.Add(outer[k]);
        for (int k = 0; k <= nh; k++) res.Add(hole[(mi + k) % nh]); // m .. around .. m
        res.Add(outer[pi]);                                          // bridge back to p
        for (int k = pi + 1; k < no; k++) res.Add(outer[k]);
        return res;
    }

    /// <summary>Bridge to the ray's first edge-HIT POINT (inserted as a new vertex), so it never shares an
    /// outline vertex — pinch-free even when several holes lie in the same horizontal band. Snaps to an
    /// endpoint only if the hit lands exactly on one (avoids a near-duplicate point).</summary>
    private static List<Vector2> BridgeEdge(Vector2[] outerIn, Vector2[] holeIn)
    {
        Vector2[] outer = EnsureWinding(outerIn, ccw: true);
        Vector2[] hole = EnsureWinding(holeIn, ccw: false);
        int no = outer.Length, nh = hole.Length;
        int mi = MaxXIndex(hole);
        Vector2 m = hole[mi];

        (int e, Vector2 hit) = FirstHit(outer, m);
        if (e < 0) return null;

        int ea = e, eb = (e + 1) % no;
        bool atA = (hit - outer[ea]).Length() < 1e-4f, atB = (hit - outer[eb]).Length() < 1e-4f;
        var res = new List<Vector2>();
        if (atA || atB)
        {
            int pi = atA ? ea : eb;
            for (int k = 0; k <= pi; k++) res.Add(outer[k]);
            for (int k = 0; k <= nh; k++) res.Add(hole[(mi + k) % nh]);
            res.Add(outer[pi]);
            for (int k = pi + 1; k < no; k++) res.Add(outer[k]);
        }
        else
        {
            for (int k = 0; k <= e; k++) res.Add(outer[k]);
            res.Add(hit);                                          // insert the hit point on edge e
            for (int k = 0; k <= nh; k++) res.Add(hole[(mi + k) % nh]);
            res.Add(hit);
            for (int k = e + 1; k < no; k++) res.Add(outer[k]);
        }
        return res;
    }

    private static int MaxXIndex(Vector2[] ring)
    {
        int mi = 0;
        for (int i = 1; i < ring.Length; i++) if (ring[i].X > ring[mi].X) mi = i;
        return mi;
    }

    private static Vector2[] EnsureWinding(Vector2[] p, bool ccw)
    {
        float area = 0;
        for (int i = 0; i < p.Length; i++) { Vector2 u = p[i], v = p[(i + 1) % p.Length]; area += u.X * v.Y - v.X * u.Y; }
        if ((area > 0) == ccw) return p;
        var r = new Vector2[p.Length];
        for (int i = 0; i < p.Length; i++) r[i] = p[p.Length - 1 - i];
        return r;
    }

    private static float Orient(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool PointInTri(Vector2 q, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Orient(a, b, q), d2 = Orient(b, c, q), d3 = Orient(c, a, q);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static SurfaceTool Begin()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        return st;
    }

    private static void Commit(SurfaceTool st, ArrayMesh mesh)
    {
        st.GenerateTangents();
        st.Commit(mesh);
    }

    private static float GetF(PrimitiveInstanceData d, string key, float def)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsSingle() : def;

    private static int GetI(PrimitiveInstanceData d, string key, int def)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsInt32() : def;
}
