using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Geometry;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A cross-section swept along a freeform path (the unified "path tool" — curved walls, banked roads,
/// half-pipe channels). The path is a list of control points stored in <c>Parameters["points"]</c>
/// (local space, relative to the instance origin); they are smoothed into a <see cref="Curve3D"/> at
/// build time and sampled into stations. At each station a rotation-minimizing frame (parallel
/// transport of an up vector) gives a (right, up) basis — so the cross-section keeps its orientation
/// through steep climbs and loops where a fixed world-up or a Frenet frame would twist or degenerate.
/// A global <c>bank</c> rolls the whole cross-section about the path tangent.
///
/// The cross-section is one of three <c>profile</c>s, each a closed 2D loop (frame coords: X = lateral,
/// Y = up) with a per-edge material slot, swept as quad strips + triangulated end caps:
///   0 Ribbon  — a flat slab you roll on (top = Surface, sides/bottom = Side); bank it for a banked road.
///               Optional edge rails (the "rail" param) fold a curb or angled-bank lip down each long edge
///               into the swept loop on a trailing Rail slot, so the rails inherit the framing/banking/caps.
///   1 Channel — a U-shell half-pipe (concave inside = Surface, outer shell + rims = Side).
///   2 Wall    — a thin upright wall / guard rail (vertical faces = Surface, top/bottom = Side).
///   3 Tube    — a full circular pipe, ball rolls inside (inner bore = Surface, outer shell = Side); for
///               loops. Two concentric rings (not a single loop), so it has its own sweep (SweepTube).
///
/// Loops are wound so the exterior normal is the edge's LEFT-normal (−dY, dX) — the same convention as
/// the verified <see cref="HalfPipePrimitive"/> sweep, so the quad-strip winding is inherited. End caps
/// triangulate the loop and orient each triangle by its geometric normal (TriangulatePolygon does not
/// guarantee a winding), so they can't silently invert.
/// </summary>
public sealed class PathSweepPrimitive : IPrimitive
{
    public string TypeId => "path_sweep";
    public string DisplayName => "Path Sweep";
    public string Category => "Curves";

    // Profile enum (the "profile" param).
    private const int Ribbon = 0, Channel = 1, Wall = 2, Tube = 3;

    // Edge-rail style ("rail" param). APPEND-ONLY (index = stored value): "Elevated Rail" is appended at 3
    // rather than slotted next to its polygon-floor twin so existing path_sweep .tres with rail=Bank (=2)
    // keep their meaning. Curb/Bank fold into the swept cross-section; Fence is emitted separately.
    private const int RailNone = 0, RailCurb = 1, RailBank = 2, RailFence = 3;

    public IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("profile", "Profile", ParamType.Int, 0, 0f, 3f,
            new[] { "Ribbon", "Channel", "Wall", "Tube" }),
        new ParamSpec("width",      "Width",       ParamType.Float, 4.0f,  0.1f, 1000f),
        new ParamSpec("thickness",  "Thickness",   ParamType.Float, 0.2f,  0.05f, 10f),
        new ParamSpec("bank",       "Bank (deg)",  ParamType.Float, 0.0f, -89f,  89f),
        new ParamSpec("wallHeight", "Wall Height", ParamType.Float, 3.0f,  0.1f, 100f),
        new ParamSpec("radius",     "Radius",      ParamType.Float, 1.5f,  0.1f, 100f),
        new ParamSpec("arc",        "Arc (deg)",   ParamType.Float, 180f,  30f,  270f),
        new ParamSpec("sides",      "Sides",       ParamType.Int,   12,    2f,   64f),
        new ParamSpec("segments",   "Segments",    ParamType.Int,   8,     1f,   64f),
        new ParamSpec("closed",     "Closed Loop", ParamType.Bool,  false),
        // Edge rails — RIBBON profile only (the other profiles are already enclosed). None = bare slab;
        // Rail = a vertical curb/lip down each long edge; Bank = an angled wedge down each edge; Elevated Rail
        // = posts + a top beam (fence) down each edge. The rolling lane runs between the two rails. These feed
        // a TRAILING, CONDITIONAL "Rail" surface/slot: surface 2 exists only when a rail is built, so the
        // positional Surface/Side mapping is undisturbed otherwise. (Enum appended-only — see RailFence above:
        // order is None/Rail/Bank/Elevated Rail, unlike the polygon floor's None/Rail/Elevated Rail/Bank.)
        new ParamSpec("rail",          "Edge Rail",       ParamType.Int,   0, 0f, 3f, new[] { "None", "Rail", "Bank", "Elevated Rail" }),
        new ParamSpec("railHeight",    "Rail Height",     ParamType.Float, 0.3f, 0.02f, 50f),
        new ParamSpec("railWidth",     "Rail Width",      ParamType.Float, 0.3f, 0.02f, 50f),
        // Bank wedge angle from horizontal (degrees): the wedge drops railHeight over a run of
        // height/tan(|angle|). SIGN sets the lean — positive = high lip at the OUTER edge sloping inward
        // (funnels toward the lane); negative = high lip at the INNER edge sloping back out. Ignored unless Bank.
        new ParamSpec("railBankAngle", "Rail Bank Angle", ParamType.Float, 45f, -85f, 85f),
    };

    // "Rail" is a TRAILING, CONDITIONAL slot (committed only for a railed Ribbon), so its absence leaves the
    // Surface/Side mapping intact for every other profile — same positional-slot convention as the baker.
    public IReadOnlyList<string> MaterialSlots { get; } = new[] { "Surface", "Side", "Rail" };

    public ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx)
    {
        (List<Vector3> pts, List<float> banks) = ReadPath(data);
        var empty = new ArrayMesh();
        if (pts.Count < 2) return empty;

        int profile = GetI(data, "profile", Ribbon);
        float width = GetF(data, "width", 4f);
        float t = GetF(data, "thickness", 0.2f);
        float globalBank = GetF(data, "bank", 0f);
        float wallH = GetF(data, "wallHeight", 3f);
        float radius = GetF(data, "radius", 1.5f);
        float arc = Mathf.DegToRad(GetF(data, "arc", 180f));
        int sides = Mathf.Max(2, GetI(data, "sides", 12));
        int segPer = Mathf.Max(1, GetI(data, "segments", 8));
        bool closed = GetB(data, "closed", false) && pts.Count >= 3; // a closed ring needs ≥3 points

        // --- Path: smooth the control points into a Curve3D (cyclic when closed), sample by baked length. ---
        Curve3D curve = BuildCurve(pts, closed);
        float len = curve.GetBakedLength();
        if (len < 1e-4f) return empty;

        // Closed: `stations` segments wrap (station[stations] coincides with station[0]); open: one fewer.
        int stations = Mathf.Max(2, segPer * (closed ? pts.Count : pts.Count - 1));
        var pos = new Vector3[stations + 1];
        var dist = new float[stations + 1];
        for (int i = 0; i <= stations; i++)
        {
            dist[i] = len * i / stations;
            pos[i] = curve.SampleBaked(dist[i], true); // closed: pos[stations] == pos[0] (curve end == start)
        }

        // --- Per-station tangent (central difference; cyclic when closed) + rotation-minimizing frame. ---
        var T = new Vector3[stations + 1];
        for (int i = 0; i <= stations; i++)
        {
            Vector3 a = closed ? pos[(i - 1 + stations) % stations] : pos[Mathf.Max(0, i - 1)];
            Vector3 b = closed ? pos[(i + 1) % stations]            : pos[Mathf.Min(stations, i + 1)];
            Vector3 d = b - a;
            T[i] = d.LengthSquared() > 1e-9f ? d.Normalized() : (i > 0 ? T[i - 1] : Vector3.Right);
        }

        var R = new Vector3[stations + 1];
        var U = new Vector3[stations + 1];
        Vector3 seed = Mathf.Abs(T[0].Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        U[0] = (seed - T[0] * seed.Dot(T[0])).Normalized();
        R[0] = T[0].Cross(U[0]).Normalized();
        for (int i = 1; i <= stations; i++)
        {
            // Parallel-transport the previous up by the minimal rotation taking T[i-1] onto T[i].
            Vector3 axis = T[i - 1].Cross(T[i]);
            float s = axis.Length(), c = T[i - 1].Dot(T[i]);
            Vector3 u = s > 1e-6f ? U[i - 1].Rotated(axis / s, Mathf.Atan2(s, c)) : U[i - 1];
            U[i] = (u - T[i] * u.Dot(T[i])).Normalized();
            R[i] = T[i].Cross(U[i]).Normalized();
        }

        // Closed-loop holonomy: parallel transport doesn't return the up to its start (a residual twist),
        // so measure the signed residual about the closing tangent (T[stations] == T[0]) and unwind it
        // linearly along the ring so the seam joins untwisted. Zero for an open path — and, correctly, for
        // a flat horizontal ring (transporting +Y about a vertical axis is the identity).
        float holonomy = closed
            ? Mathf.Atan2(U[stations].Cross(U[0]).Dot(T[0]), U[stations].Dot(U[0]))
            : 0f;

        // Bank: roll the frame about the tangent by global bank + per-point bank (interpolated by arc
        // offset) + the distributed holonomy unwind. Control-point offsets come from the baked curve; for a
        // closed ring the list wraps (offset len ↦ the first point's bank) so the bank stays seam-continuous.
        int nc = pts.Count;
        var cpOff = new float[closed ? nc + 1 : nc];
        for (int k = 0; k < nc; k++) cpOff[k] = curve.GetClosestOffset(pts[k]);
        cpOff[0] = 0f;
        List<float> bankList = banks;
        if (closed) { cpOff[nc] = len; bankList = new List<float>(banks) { banks[0] }; }
        else cpOff[nc - 1] = len;

        for (int i = 0; i <= stations; i++)
        {
            float bankDeg = globalBank + BankAt(dist[i], cpOff, bankList)
                          + Mathf.RadToDeg(holonomy) * ((float)i / stations);
            if (Mathf.Abs(bankDeg) < 1e-4f) continue;
            float a = Mathf.DegToRad(bankDeg);
            R[i] = R[i].Rotated(T[i], a);
            U[i] = U[i].Rotated(T[i], a);
        }

        int railStyle = GetI(data, "rail", RailNone);
        bool hasRail = profile == Ribbon && railStyle != RailNone; // rails only on the open Ribbon profile
        // Curb/Bank fold their lip into the swept cross-section; the Fence (posts + beam) can't be a constant
        // cross-section, so it keeps the plain lane loop and is emitted separately onto the Rail slot below.
        bool foldedRail = hasRail && railStyle != RailFence;
        bool fenceRail = hasRail && railStyle == RailFence;

        SurfaceTool surface = Begin(), side = Begin(), rail = Begin();
        if (profile == Tube)
        {
            // A full pipe is two concentric rings, not a simple loop (an annulus can't be one
            // TriangulatePolygon polygon), so it gets its own sweep with annular end caps.
            SweepTube(surface, side, pos, R, U, T, dist, radius, t, sides, closed);
        }
        else
        {
            // --- Cross-section loop + per-edge slot for the chosen profile. A railed Ribbon folds its curb /
            // bank lips straight into the swept loop (slot 2 = Rail), so they inherit the framing/banking/caps. ---
            (Vector2[] loop, int[] slot) = profile switch
            {
                Channel => ChannelLoop(radius, t, arc, sides),
                Wall    => WallLoop(t, wallH),
                _ when foldedRail => RibbonRailLoop(width, t, railStyle,
                                      GetF(data, "railHeight", 0.3f), GetF(data, "railWidth", 0.3f),
                                      GetF(data, "railBankAngle", 45f)),
                _       => RibbonLoop(width, t),
            };
            Sweep(surface, side, rail, pos, R, U, T, dist, loop, slot, closed);

            if (fenceRail)
                EmitRibbonFence(rail, pos, R, U, T, dist, width,
                    GetF(data, "railHeight", 0.3f), GetF(data, "railWidth", 0.3f), closed);
        }

        var mesh = new ArrayMesh();
        Commit(surface, mesh);
        Commit(side, mesh);
        if (hasRail) Commit(rail, mesh); // trailing Rail surface — only when rail geometry was emitted
        return mesh;
    }

    public Shape3D[] BuildCollision(PrimitiveInstanceData data, BuildContext ctx)
    {
        ArrayMesh mesh = BuildMesh(data, ctx);
        if (mesh.GetSurfaceCount() == 0) return System.Array.Empty<Shape3D>();
        return new Shape3D[] { mesh.CreateTrimeshShape() };
    }

    // --- Cross-section profiles. Frame coords: X = lateral (right), Y = up. Wound exterior-on-left
    // (outward normal = the edge's left-normal (−dY, dX)). slot[e] routes edge e: 0 = Surface, 1 = Side.

    private static (Vector2[], int[]) RibbonLoop(float w, float t)
    {
        float h = w * 0.5f;
        var loop = new[] { new Vector2(-h, 0), new Vector2(h, 0), new Vector2(h, -t), new Vector2(-h, -t) };
        return (loop, new[] { 0, 1, 1, 1 }); // top = Surface, right/bottom/left = Side
    }

    /// <summary>The Ribbon cross-section with an edge rail folded in (slot 2 = Rail): the rolling top (slot 0)
    /// runs between a curb / bank lip down each long edge, the outer sides + bottom stay Side (slot 1). The
    /// rail width is clamped to leave a rolling lane between the two lips. Style 1 = vertical curb (rise rh,
    /// width rw); style 2 = angled Bank — the wedge drops rh over a run of rh/tan(|angle|), and the angle's
    /// SIGN leans it: positive puts the high lip on the OUTER edge (slopes inward toward the lane), negative on
    /// the INNER edge (slopes back out). Both wind exterior-on-left like <see cref="RibbonLoop"/>.</summary>
    private static (Vector2[], int[]) RibbonRailLoop(float w, float t, int style, float rh, float rw, float bankAngleDeg)
    {
        float h = w * 0.5f;
        if (style == 2) // Bank: angled wedge, run derived from the angle, sign leans it.
        {
            bool flip = bankAngleDeg < 0f;
            float mag = Mathf.Clamp(Mathf.Abs(bankAngleDeg), 5f, 85f);
            float run = Mathf.Min(rh / Mathf.Tan(Mathf.DegToRad(mag)), 0.45f * w);
            Vector2[] bank = flip
                // High lip at the INNER edge, slope back out to the ribbon edge.
                ? new[] { new Vector2(-h, 0), new Vector2(-h + run, rh), new Vector2(-h + run, 0),
                          new Vector2(h - run, 0), new Vector2(h - run, rh), new Vector2(h, 0),
                          new Vector2(h, -t), new Vector2(-h, -t) }
                // High lip at the OUTER edge, slope inward toward the lane.
                : new[] { new Vector2(-h, 0), new Vector2(-h, rh), new Vector2(-h + run, 0),
                          new Vector2(h - run, 0), new Vector2(h, rh), new Vector2(h, 0),
                          new Vector2(h, -t), new Vector2(-h, -t) };
            return (bank, new[] { 2, 2, 0, 2, 2, 1, 1, 1 });
        }

        // Style 1: vertical curb down each edge — outer wall, top band, inner wall.
        rw = Mathf.Min(rw, 0.45f * w);
        var curb = new[]
        {
            new Vector2(-h, 0),      new Vector2(-h, rh),      new Vector2(-h + rw, rh), new Vector2(-h + rw, 0),
            new Vector2(h - rw, 0),  new Vector2(h - rw, rh),  new Vector2(h, rh),       new Vector2(h, 0),
            new Vector2(h, -t),      new Vector2(-h, -t),
        };
        return (curb, new[] { 2, 2, 2, 0, 2, 2, 2, 1, 1, 1 });
    }

    /// <summary>Elevated rail (fence) for the Ribbon: square posts at ~2.5 m intervals down EACH long edge
    /// plus a continuous top beam, swept along the path onto the Rail slot. Unlike the curb/bank lips this
    /// isn't a constant cross-section (the posts are discrete), so it's emitted separately AFTER the lane
    /// sweep rather than folded into the loop. Post/beam cross-section = railW (clamped so the two edges'
    /// posts can't meet across the lane); the top beam is flush at railH.</summary>
    private static void EmitRibbonFence(SurfaceTool rail, Vector3[] pos, Vector3[] R, Vector3[] U, Vector3[] T,
        float[] dist, float width, float railH, float railW, bool closed)
    {
        int stations = pos.Length - 1;
        float h = width * 0.5f;
        railW = Mathf.Min(railW, 0.45f * width); // keep posts off the rolling lane (and out of each other)
        float half = railW * 0.5f;
        const float spacing = 2.5f;

        foreach (int e in new[] { -1, 1 }) // both long edges (lateral ±h)
        {
            float lat = e * h;

            // Posts: one at the start, then every ~spacing metres of path length. On a closed ring skip the
            // final station (it coincides with station 0 → no doubled post); an open path also caps the end.
            float since = spacing; // force a post at station 0
            int last = closed ? stations - 1 : stations;
            for (int i = 0; i <= last; i++)
            {
                if (i > 0) since += dist[i] - dist[i - 1];
                bool end = !closed && i == stations;
                if (since < spacing && !end) continue;
                since = 0f;
                Vector3 c = pos[i] + lat * R[i] + (railH * 0.5f) * U[i];
                AddOrientedBox(rail, c, R[i] * half, U[i] * (railH * 0.5f), T[i] * half);
            }

            // Top beam: a railW×railW square section centred at (lat, railH−half), swept along all stations on
            // the Rail slot. Passing `rail` for all three Sweep tools keeps every quad (slot 0 → 1st arg) and
            // end cap (→ 2nd arg) on Rail. Wound CW (top-left, top-right, bottom-right, bottom-left) to match
            // RibbonLoop's exterior-on-left convention so the beam faces out/up.
            float cy = railH - half;
            var beam = new[]
            {
                new Vector2(lat - half, cy + half), new Vector2(lat + half, cy + half),
                new Vector2(lat + half, cy - half), new Vector2(lat - half, cy - half),
            };
            Sweep(rail, rail, rail, pos, R, U, T, dist, beam, new[] { 0, 0, 0, 0 }, closed);
        }
    }

    /// <summary>Adds a box from a centre and three half-extent axis vectors (any orientation). Each of the 6
    /// faces is emitted via <see cref="MeshBuilder.AddQuadFacing"/> toward its outward axis, so winding is
    /// auto-resolved. (Mirrors the polygon floor's fence-post box.)</summary>
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

    private static (Vector2[], int[]) WallLoop(float t, float h)
    {
        float hx = t * 0.5f;
        var loop = new[] { new Vector2(-hx, h), new Vector2(hx, h), new Vector2(hx, 0), new Vector2(-hx, 0) };
        return (loop, new[] { 1, 0, 1, 0 }); // top = Side, right/left vertical = Surface, bottom = Side
    }

    private static (Vector2[], int[]) ChannelLoop(float r, float t, float arc, int sides)
    {
        float half = arc * 0.5f;
        int count = 2 * (sides + 1);
        var loop = new Vector2[count];
        var slot = new int[count];
        // Inner concave arc (Surface), left rim → bottom → right rim.
        for (int k = 0; k <= sides; k++)
        {
            float phi = -half + arc * k / sides;
            loop[k] = new Vector2(r * Mathf.Sin(phi), r - r * Mathf.Cos(phi));
        }
        // Outer shell arc (Side), right rim → bottom → left rim.
        for (int k = 0; k <= sides; k++)
        {
            float phi = half - arc * k / sides;
            loop[sides + 1 + k] = new Vector2((r + t) * Mathf.Sin(phi), r - (r + t) * Mathf.Cos(phi));
        }
        // First `sides` edges are the inner arc (Surface); the rims + outer arc are Side.
        for (int e = 0; e < count; e++) slot[e] = e < sides ? 0 : 1;
        return (loop, slot);
    }

    // --- Sweep the closed loop along the stations: one quad strip per edge + triangulated end caps. ---

    private static void Sweep(SurfaceTool surface, SurfaceTool side, SurfaceTool rail, Vector3[] pos, Vector3[] R, Vector3[] U,
        Vector3[] T, float[] dist, Vector2[] loop, int[] slot, bool closed)
    {
        int stations = pos.Length - 1;
        int n = loop.Length;

        var perim = new float[n + 1]; // V = cumulative loop perimeter (world-unit UVs)
        for (int e = 0; e < n; e++) perim[e + 1] = perim[e] + loop[e].DistanceTo(loop[(e + 1) % n]);

        Vector3 Frame(int i, Vector2 q) => pos[i] + q.X * R[i] + q.Y * U[i];

        for (int e = 0; e < n; e++)
        {
            Vector2 pe = loop[e], pf = loop[(e + 1) % n];
            Vector2 d = pf - pe;
            var ln = new Vector2(-d.Y, d.X);
            if (ln.LengthSquared() < 1e-12f) continue; // degenerate edge (e.g. a closed-tube rim)
            ln = ln.Normalized();
            SurfaceTool st = slot[e] switch { 0 => surface, 2 => rail, _ => side };
            float vE = perim[e], vF = perim[e + 1];
            for (int i = 0; i < stations; i++)
            {
                int j = i + 1;
                Vector3 nrm = (ln.X * R[i] + ln.Y * U[i]).Normalized();
                MeshBuilder.AddQuad(st, Frame(i, pe), Frame(i, pf), Frame(j, pf), Frame(j, pe), nrm,
                    new Vector2(dist[i], vE), new Vector2(dist[i], vF),
                    new Vector2(dist[j], vF), new Vector2(dist[j], vE));
            }
        }

        // A closed ring has no ends — the final quad (station stations-1 → stations, where pos[stations]
        // and the holonomy-corrected frame both equal station 0's) seals it. Open paths get end caps.
        if (!closed)
        {
            AddCap(side, loop, 0, pos, R, U, -T[0]);
            AddCap(side, loop, stations, pos, R, U, T[stations]);
        }
    }

    /// <summary>
    /// Sweeps a full circular pipe (the Tube profile): an inner ring (Surface, the bore the ball rolls
    /// inside, normals facing the axis) and a concentric outer ring (Side, normals out). The circle is
    /// centred so its lowest point sits on the path (frame (0,0)) — same convention as the channel, so a
    /// closed channel and a tube line up. Open paths get an annular ring end cap; a closed ring needs none
    /// (the wrap quad meets the holonomy-matched seam frame). Winding mirrors the verified channel sweep.
    /// </summary>
    private static void SweepTube(SurfaceTool surface, SurfaceTool side, Vector3[] pos, Vector3[] R, Vector3[] U,
        Vector3[] T, float[] dist, float r, float t, int sides, bool closed)
    {
        int stations = pos.Length - 1;
        var inL = new float[sides + 1]; var inU = new float[sides + 1];
        var outL = new float[sides + 1]; var outU = new float[sides + 1];
        var radL = new float[sides + 1]; var radU = new float[sides + 1]; // outward radial unit (lateral, up)
        var vArc = new float[sides + 1];
        for (int k = 0; k <= sides; k++)
        {
            float th = Mathf.Tau * k / sides;
            float c = Mathf.Cos(th), s = Mathf.Sin(th);
            inL[k] = r * s;          inU[k] = r - r * c;            // θ=0 → (0,0) bottom, on the path
            outL[k] = (r + t) * s;   outU[k] = r - (r + t) * c;
            radL[k] = s;             radU[k] = -c;                  // from the circle centre (0,r) outward
            vArc[k] = r * th;
        }

        Vector3 In(int i, int k) => pos[i] + inL[k] * R[i] + inU[k] * U[i];
        Vector3 Out(int i, int k) => pos[i] + outL[k] * R[i] + outU[k] * U[i];
        Vector3 Rad(int i, int k) => (radL[k] * R[i] + radU[k] * U[i]).Normalized();

        for (int i = 0; i < stations; i++)
        {
            int j = i + 1;
            for (int k = 0; k < sides; k++)
            {
                int m = k + 1;
                Vector3 nIn = -(Rad(i, k) + Rad(i, m) + Rad(j, m) + Rad(j, k)).Normalized(); // toward the axis
                MeshBuilder.AddQuad(surface, In(i, k), In(i, m), In(j, m), In(j, k), nIn,
                    new Vector2(dist[i], vArc[k]), new Vector2(dist[i], vArc[m]),
                    new Vector2(dist[j], vArc[m]), new Vector2(dist[j], vArc[k]));
                MeshBuilder.AddQuad(side, Out(i, m), Out(i, k), Out(j, k), Out(j, m), -nIn); // outward shell
            }
        }

        if (!closed)
            for (int k = 0; k < sides; k++)
            {
                int m = k + 1;
                MeshBuilder.AddQuadFacing(side, In(0, k), Out(0, k), Out(0, m), In(0, m), -T[0]);
                MeshBuilder.AddQuadFacing(side, In(stations, k), Out(stations, k), Out(stations, m), In(stations, m), T[stations]);
            }
    }

    /// <summary>
    /// One end cap: triangulate the cross-section loop in frame 2D coords and emit it at station
    /// <paramref name="i"/>. TriangulatePolygon's winding isn't guaranteed, so each triangle is oriented
    /// by its own geometric normal toward <paramref name="refN"/> (the outward face direction).
    /// </summary>
    private static void AddCap(SurfaceTool side, Vector2[] loop, int i, Vector3[] pos, Vector3[] R, Vector3[] U,
        Vector3 refN)
    {
        int[] tris = Geometry2D.TriangulatePolygon(loop);
        if (tris == null || tris.Length < 3) return;
        Vector3 P(Vector2 q) => pos[i] + q.X * R[i] + q.Y * U[i];
        for (int k = 0; k + 2 < tris.Length; k += 3)
        {
            Vector2 a2 = loop[tris[k]], b2 = loop[tris[k + 1]], c2 = loop[tris[k + 2]];
            Vector3 a = P(a2), b = P(b2), c = P(c2);
            Vector3 nrm = (b - a).Cross(c - a);
            if (nrm.LengthSquared() < 1e-12f) continue;
            nrm = nrm.Normalized();
            if (nrm.Dot(refN) >= 0) MeshBuilder.AddTri(side, a, b, c, nrm, a2, b2, c2);
            else MeshBuilder.AddTri(side, a, c, b, -nrm, a2, c2, b2);
        }
    }

    // --- Path smoothing: Catmull-Rom-style Bezier handles on a Curve3D. ---

    private static Curve3D BuildCurve(List<Vector3> pts, bool closed)
    {
        var curve = new Curve3D { BakeInterval = 0.05f };
        int n = pts.Count;
        // Closed: emit n+1 points (the last repeats point 0) with cyclic tangents, so the ring closes
        // smoothly through point 0 with a matching tangent on both sides of the seam.
        int count = closed ? n + 1 : n;
        for (int i = 0; i < count; i++)
        {
            int idx = i % n;
            Vector3 prev = closed ? pts[(idx - 1 + n) % n] : pts[Mathf.Max(0, idx - 1)];
            Vector3 next = closed ? pts[(idx + 1) % n]     : pts[Mathf.Min(n - 1, idx + 1)];
            Vector3 dir = next - prev;
            dir = dir.LengthSquared() > 1e-9f ? dir.Normalized() : Vector3.Right;
            float inLen = pts[idx].DistanceTo(prev) / 3f;
            float outLen = pts[idx].DistanceTo(next) / 3f;
            curve.AddPoint(pts[idx], -dir * inLen, dir * outLen);
        }
        return curve;
    }

    /// <summary>Reads control points and their parallel per-point bank (degrees), dropping near-coincident
    /// consecutive points (zero-length segments produce NaN tangents — the live preview's hovered point is
    /// often a duplicate of the last) and their banks in lockstep so the two lists stay index-aligned.</summary>
    private static (List<Vector3>, List<float>) ReadPath(PrimitiveInstanceData data)
    {
        var pts = new List<Vector3>();
        var banks = new List<float>();
        if (!data.Parameters.ContainsKey("points")) return (pts, banks);

        Godot.Collections.Array rawPts = data.Parameters["points"].AsGodotArray();
        Godot.Collections.Array rawBanks = data.Parameters.ContainsKey("banks")
            ? data.Parameters["banks"].AsGodotArray() : new Godot.Collections.Array();
        for (int i = 0; i < rawPts.Count; i++)
        {
            Vector3 p = rawPts[i].AsVector3();
            if (pts.Count > 0 && p.DistanceTo(pts[^1]) <= 1e-3f) continue;
            pts.Add(p);
            banks.Add(i < rawBanks.Count ? rawBanks[i].AsSingle() : 0f);
        }
        return (pts, banks);
    }

    /// <summary>Per-point bank (degrees) interpolated at baked offset <paramref name="d"/>: find the
    /// control-point interval containing it and lerp the bracketing banks.</summary>
    private static float BankAt(float d, float[] cpOff, List<float> banks)
    {
        int n = banks.Count;
        if (n == 0) return 0f;
        if (n == 1) return banks[0];
        for (int k = 0; k < n - 1; k++)
        {
            if (d > cpOff[k + 1] && k < n - 2) continue;
            float seg = cpOff[k + 1] - cpOff[k];
            float tt = seg > 1e-5f ? Mathf.Clamp((d - cpOff[k]) / seg, 0f, 1f) : 0f;
            return Mathf.Lerp(banks[k], banks[k + 1], tt);
        }
        return banks[n - 1];
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

    private static bool GetB(PrimitiveInstanceData d, string key, bool def)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsBool() : def;
}
