using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Geometry;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A swept U-channel / half-pipe: a constant-thickness curved shell whose concave inside keeps a
/// rolling ball centred. The channel follows a path that leaves the local origin heading along +X,
/// optionally turning <c>curve</c>° in the horizontal plane and climbing <c>rise</c> metres over its
/// horizontal run <c>length</c>. The cross-section is a circular arc of <c>radius</c> sweeping
/// <c>arc</c>° symmetric about the bottom (lowest point on the path centreline), built in an UPRIGHT
/// frame — up is always world +Y, lateral is horizontal and perpendicular to the heading — so the
/// opening stays up no matter how steep the rise, and the horizontal path is exactly the
/// banked-curve arc math. The shell has constant <c>thickness</c> (outer arc concentric), and the
/// path is tessellated into <c>segments</c> stations along its length × <c>sides</c> across the arc.
///
/// Surfaces: 0 Surface (the concave inside the ball rolls on), 1 Side (outer shell, the two rim caps,
/// the two end caps). Inner-surface winding mirrors RampPrimitive's verified up-facing top quad.
/// The geometry is anchored at the entry (path f=0 at the origin), like the banked curve.
/// </summary>
public sealed class HalfPipePrimitive : IPrimitive
{
    public string TypeId => "half_pipe";
    public string DisplayName => "Half-Pipe";
    public string Category => "Curves";

    public IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("length",    "Length",     ParamType.Float, 4.0f,  0.1f,   1000f),
        new ParamSpec("radius",    "Radius",     ParamType.Float, 1.5f,  0.1f,   100f),
        new ParamSpec("arc",       "Arc (deg)",  ParamType.Float, 180f,  30f,    360f),
        new ParamSpec("curve",     "Curve (deg)",ParamType.Float, 0.0f, -270f,   270f),
        new ParamSpec("rise",      "Rise",       ParamType.Float, 0.0f,  0f,     200f),
        new ParamSpec("deck",      "Deck",       ParamType.Bool,  false),
        new ParamSpec("deckWidth", "Deck Width", ParamType.Float, 1.0f,  0.05f,  100f),
        new ParamSpec("thickness", "Thickness",  ParamType.Float, 0.2f,  0.05f,  10f),
        new ParamSpec("sides",     "Sides",      ParamType.Int,   12,    2f,     64f),
        new ParamSpec("segments",  "Segments",   ParamType.Int,   16,    1f,     128f),
    };

    public IReadOnlyList<string> MaterialSlots { get; } = new[] { "Surface", "Side" };

    public ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx)
    {
        float length = GetF(data, "length", 4f);
        float radius = GetF(data, "radius", 1.5f);
        float arcDeg = GetF(data, "arc", 180f);
        float curveDeg = GetF(data, "curve", 0f);
        float rise = GetF(data, "rise", 0f);
        bool deckOn = GetB(data, "deck", false);
        float deckW = GetF(data, "deckWidth", 1f);
        float t = GetF(data, "thickness", 0.2f);
        int sides = Mathf.Max(2, GetI(data, "sides", 12));
        int seg = Mathf.Max(1, GetI(data, "segments", 16));

        float arc = Mathf.DegToRad(arcDeg);
        float half = arc * 0.5f;
        var up = Vector3.Up;

        // At ~360° the rims meet — it's a closed tube, so the rim caps degenerate (skip them). Decks
        // (flat skater platforms at the rims) only make sense while the rims still face up (arc ≤ 180).
        bool tube = arcDeg >= 359f;
        bool decked = !tube && arcDeg <= 180.5f && deckOn && deckW > 0.001f;

        // --- Path stations (upright frame): position P, lateral L (horizontal ⟂ heading), heading Th. ---
        float curveRad = Mathf.DegToRad(curveDeg);
        bool straight = Mathf.Abs(curveRad) < 1e-4f;
        float sgn = curveDeg >= 0 ? 1f : -1f;
        float rh = straight ? 0f : length / Mathf.Abs(curveRad); // horizontal turn radius
        float runLen = Mathf.Sqrt(length * length + rise * rise);  // centreline length, for continuous U

        var P = new Vector3[seg + 1];
        var L = new Vector3[seg + 1];
        var Th = new Vector3[seg + 1];
        var pathU = new float[seg + 1];
        for (int i = 0; i <= seg; i++)
        {
            float f = (float)i / seg;
            Vector3 hpos, th;
            if (straight)
            {
                hpos = new Vector3(length * f, 0, 0);
                th = new Vector3(1, 0, 0);
            }
            else
            {
                float theta = Mathf.Abs(curveRad) * f;
                float ct = Mathf.Cos(theta), st = Mathf.Sin(theta);
                hpos = new Vector3(rh * st, 0, sgn * (rh * ct - rh)); // turn toward ∓Z for curve ≷ 0
                th = new Vector3(ct, 0, -sgn * st);
            }
            P[i] = new Vector3(hpos.X, rise * f, hpos.Z);
            Th[i] = th;
            L[i] = th.Cross(up).Normalized(); // +X heading → +Z lateral
            pathU[i] = runLen * f;
        }

        // --- Cross-section (constant along the path): inner/outer arc + inner-surface normal split. ---
        var inH = new float[sides + 1]; var inZ = new float[sides + 1];
        var outH = new float[sides + 1]; var outZ = new float[sides + 1];
        var nUp = new float[sides + 1]; var nLat = new float[sides + 1];
        var arcV = new float[sides + 1];
        for (int k = 0; k <= sides; k++)
        {
            float phi = -half + arc * k / sides;
            float c = Mathf.Cos(phi), s = Mathf.Sin(phi);
            inH[k] = radius - radius * c; inZ[k] = radius * s;
            outH[k] = radius - (radius + t) * c; outZ[k] = (radius + t) * s;
            nUp[k] = c; nLat[k] = -s;          // at φ=0 → up; at the rims → inward
            arcV[k] = radius * (phi + half);
        }

        Vector3 In(int i, int k) => P[i] + inH[k] * up + inZ[k] * L[i];
        Vector3 Out(int i, int k) => P[i] + outH[k] * up + outZ[k] * L[i];
        Vector3 NIn(int i, int k) => nUp[k] * up + nLat[k] * L[i];

        SurfaceTool surface = Begin(), side = Begin();

        for (int i = 0; i < seg; i++)
        {
            int j = i + 1;
            for (int k = 0; k < sides; k++)
            {
                int m = k + 1;
                Vector3 nIn = (NIn(i, k) + NIn(i, m) + NIn(j, m) + NIn(j, k)).Normalized();

                // Concave inside (Surface): near-low, near-high, far-high, far-low from the inward normal.
                // U along the path, V along the arc — both continuous so the texture flows down the channel.
                MeshBuilder.AddQuad(surface, In(i, k), In(i, m), In(j, m), In(j, k), nIn,
                    new Vector2(pathU[i], arcV[k]), new Vector2(pathU[i], arcV[m]),
                    new Vector2(pathU[j], arcV[m]), new Vector2(pathU[j], arcV[k]));

                // Outer shell (Side): reversed winding + outward normal.
                MeshBuilder.AddQuad(side, Out(i, m), Out(i, k), Out(j, k), Out(j, m), -nIn);
            }
        }

        Vector3 t0 = (length * Th[0] + rise * up).Normalized();
        Vector3 tN = (length * Th[seg] + rise * up).Normalized();

        // Rim caps (Side): the inner→outer face at each rim, swept the length of the path. A closed tube
        // has no rims, so skip them there. (Kept even when decked — they close the shell's top edge,
        // and the deck starts just outboard at the outer rim, so there's no overlap.)
        if (!tube)
            for (int i = 0; i < seg; i++)
            {
                AddRim(side, i, In, Out, 0, rimRight: false);
                AddRim(side, i, In, Out, sides, rimRight: true);
            }

        // End caps (Side): the annular cross-section at each end, facing along ∓ the path tangent.
        for (int k = 0; k < sides; k++)
        {
            int m = k + 1;
            MeshBuilder.AddQuad(side, In(0, k), Out(0, k), Out(0, m), In(0, m), -t0);
            MeshBuilder.AddQuad(side, In(seg, m), Out(seg, m), Out(seg, k), In(seg, k), tN);
        }

        // Decks / banks (Side + Surface): a flat platform at each rim where a skater would stand. Each is
        // a constant-thickness slab starting at the outer rim and extending horizontally outward by `deck`,
        // swept along the path. Built as a closed cross-section loop wound so the exterior is on the LEFT
        // of each edge — the same convention as the verified inner surface above (left-normal = +nIn).
        if (decked)
        {
            var rUp = outH[sides]; var rLat = outZ[sides];     // outer rim, right side (+lateral)
            SweepLoop(surface, side, P, L, seg, t0, tN,
                new[] { new Vector2(rLat, rUp), new Vector2(rLat + deckW, rUp),
                        new Vector2(rLat + deckW, rUp - t), new Vector2(rLat, rUp - t) },
                new[] { surface, side, side, side });

            var lUp = outH[0]; var lLat = outZ[0];             // outer rim, left side (−lateral)
            SweepLoop(surface, side, P, L, seg, t0, tN,
                new[] { new Vector2(lLat, lUp), new Vector2(lLat, lUp - t),
                        new Vector2(lLat - deckW, lUp - t), new Vector2(lLat - deckW, lUp) },
                new[] { side, side, side, surface });
        }

        var mesh = new ArrayMesh();
        Commit(surface, mesh);
        Commit(side, mesh);
        return mesh;
    }

    /// <summary>
    /// One rim cap segment: the inner→outer face at the top of the wall at rim index <paramref name="k"/>,
    /// from station i to i+1. It's the wall's TOP edge, so it faces the sky — wound CCW-from-up using the
    /// verified floor-top convention (lowPath/lowLat → … → highPath/lowLat). "Outer" sits at higher lateral
    /// on the right rim and lower lateral on the left, so the two rims order their corners oppositely.
    /// </summary>
    private static void AddRim(SurfaceTool side, int i, System.Func<int, int, Vector3> In,
        System.Func<int, int, Vector3> Out, int k, bool rimRight)
    {
        int j = i + 1;
        Vector3 innerI = In(i, k), innerJ = In(j, k), outerJ = Out(j, k), outerI = Out(i, k);
        if (rimRight) MeshBuilder.AddQuad(side, innerI, outerI, outerJ, innerJ, Vector3.Up);
        else MeshBuilder.AddQuad(side, outerI, innerI, innerJ, outerJ, Vector3.Up);
    }

    /// <summary>
    /// Sweeps a closed cross-section loop (frame coords: X = lateral, Y = up) along the path stations,
    /// one quad strip per edge, routed to the per-edge SurfaceTool in <paramref name="edgeTool"/>, plus
    /// triangle-fan end caps on <paramref name="side"/>. The loop must be wound so the exterior is on the
    /// LEFT of each edge direction — then the left-normal (−dY, dX) matches the verified inner-surface
    /// winding (same vertex order, same normal). Caps assume a convex loop (the deck is a rectangle).
    /// </summary>
    private static void SweepLoop(SurfaceTool surface, SurfaceTool side, Vector3[] P, Vector3[] L, int seg,
        Vector3 t0, Vector3 tN, Vector2[] loop, SurfaceTool[] edgeTool)
    {
        var up = Vector3.Up;
        int n = loop.Length;
        Vector3 Frame(int i, Vector2 q) => P[i] + q.X * L[i] + q.Y * up;

        for (int e = 0; e < n; e++)
        {
            Vector2 pA = loop[e], pB = loop[(e + 1) % n];
            Vector2 d = pB - pA;
            var ln = new Vector2(-d.Y, d.X).Normalized(); // exterior normal (left of the edge), frame coords
            SurfaceTool st = edgeTool[e];
            for (int i = 0; i < seg; i++)
            {
                int j = i + 1;
                Vector3 nA = (ln.X * L[i] + ln.Y * up).Normalized();
                MeshBuilder.AddQuad(st, Frame(i, pA), Frame(i, pB), Frame(j, pB), Frame(j, pA), nA);
            }
        }

        // End caps: fan from loop[0]; reversed corner order at the two ends to match their opposite
        // normals (the start faces −t0, the end faces +tN — front faces point outward from the slab).
        Vector2 p0 = loop[0];
        for (int e = 1; e < n - 1; e++)
        {
            Vector2 pe = loop[e], pf = loop[e + 1];
            MeshBuilder.AddTri(side, Frame(0, p0), Frame(0, pf), Frame(0, pe), -t0, p0, pf, pe);
            MeshBuilder.AddTri(side, Frame(seg, p0), Frame(seg, pe), Frame(seg, pf), tN, p0, pe, pf);
        }
    }

    public Shape3D[] BuildCollision(PrimitiveInstanceData data, BuildContext ctx)
        => new Shape3D[] { BuildMesh(data, ctx).CreateTrimeshShape() };

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
