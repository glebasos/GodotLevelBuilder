using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Geometry;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A floor swept through a circular arc — the signature Super Monkey Ball turn. The walkable
/// centreline starts at the local origin heading along local +X and curves toward −Z for a positive
/// <c>arc</c> (a left turn) or toward +Z for a negative <c>arc</c> (a right turn — same forward heading,
/// mirrored across the path). It sweeps |<c>arc</c>| degrees at centreline <c>radius</c>, spanning
/// <c>width</c> across the path. The cross-section can be <c>bank</c>ed (outer edge raised, rotated about
/// the path tangent) and is a constant-<c>thickness</c> slab whose underside is offset straight down
/// (−Y) — never self-intersects below a 90° bank, and the thickness variation is invisible.
///
/// Built by stitching <c>segments</c> quad rings along the arc. Surfaces: 0 Surface (the walkable top),
/// 1 Side (underside, inner + outer walls, and the two end caps). Winding mirrors RampPrimitive's
/// verified top quad (near-inner, near-outer, far-outer, far-inner from the up-facing normal); a right
/// turn is a Z-reflection of that, so every quad's winding is reversed to keep front faces outward.
/// </summary>
public sealed class BankedCurvePrimitive : IPrimitive
{
    public string TypeId => "banked_curve";
    public string DisplayName => "Banked Curve";
    public string Category => "Curves";

    public IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("radius",    "Radius",    ParamType.Float, 4.0f,  0.5f,  100f),
        new ParamSpec("arc",       "Arc (deg)", ParamType.Float, 90.0f, -270f,  270f),
        new ParamSpec("width",     "Width",     ParamType.Float, 2.0f,  0.1f,  100f),
        new ParamSpec("bank",      "Bank (deg)",ParamType.Float, 0.0f,  0f,    60f),
        new ParamSpec("thickness", "Thickness", ParamType.Float, 0.2f,  0.05f, 10f),
        new ParamSpec("segments",  "Segments",  ParamType.Int,   16,    2f,    64f),
    };

    public IReadOnlyList<string> MaterialSlots { get; } = new[] { "Surface", "Side" };

    public ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx)
    {
        float radius = GetF(data, "radius", 4f);
        float arcDeg = GetF(data, "arc", 90f);
        float width = GetF(data, "width", 2f);
        float bankDeg = GetF(data, "bank", 0f);
        float t = GetF(data, "thickness", 0.2f);
        int seg = Mathf.Max(2, GetI(data, "segments", 16));

        // arcDeg's sign picks the turn direction: + curves toward −Z (left), − toward +Z (right). The
        // right turn is a Z-reflection of the left, so `dir` flips the Z of every position/normal and the
        // quad helpers below reverse winding to keep front faces outward.
        float dir = arcDeg >= 0 ? 1f : -1f;
        float arc = Mathf.Abs(Mathf.DegToRad(arcDeg));
        float beta = Mathf.DegToRad(bankDeg);
        float cosB = Mathf.Cos(beta), sinB = Mathf.Sin(beta);
        float halfW = width * 0.5f;
        var down = new Vector3(0, -t, 0);

        // One ring of four corners + the path normal/tangent per arc station.
        var innerTop = new Vector3[seg + 1];
        var outerTop = new Vector3[seg + 1];
        var innerBot = new Vector3[seg + 1];
        var outerBot = new Vector3[seg + 1];
        var topN = new Vector3[seg + 1]; // surface up-normal
        var arcU = new float[seg + 1];   // arc length for continuous U tiling

        for (int i = 0; i <= seg; i++)
        {
            float th = arc * i / seg;
            float s = Mathf.Sin(th), c = Mathf.Cos(th);
            // Centre of curvature at (0,0,−dir·R): centreline P(θ)=(R·sinθ, 0, dir·(R·cosθ−R)),
            // tangent (cosθ,0,−dir·sinθ). dir=+1 curves toward −Z, dir=−1 toward +Z (forward heading kept).
            var p = new Vector3(radius * s, 0, dir * (radius * c - radius));
            var radialOut = new Vector3(s, 0, dir * c);                 // away from centre (toward outer edge)
            var lateral = cosB * radialOut + sinB * Vector3.Up;          // banked across-path axis (outer raised)
            innerTop[i] = p - halfW * lateral;
            outerTop[i] = p + halfW * lateral;
            innerBot[i] = innerTop[i] + down;
            outerBot[i] = outerTop[i] + down;
            topN[i] = (-sinB * radialOut + cosB * Vector3.Up).Normalized();
            arcU[i] = radius * th;
        }

        SurfaceTool surface = Begin(), side = Begin();

        for (int i = 0; i < seg; i++)
        {
            int j = i + 1;
            Vector3 n = ((topN[i] + topN[j]) * 0.5f).Normalized();

            // Walkable top: near-inner, near-outer, far-outer, far-inner (CCW from above, like the ramp top).
            // U runs along the arc length, V across the width, so the texture tiles continuously round the turn.
            QuadUV(surface, innerTop[i], outerTop[i], outerTop[j], innerTop[j], n,
                new Vector2(arcU[i], 0), new Vector2(arcU[i], width),
                new Vector2(arcU[j], width), new Vector2(arcU[j], 0));

            // Underside (faces roughly down).
            Quad(side, outerBot[i], innerBot[i], innerBot[j], outerBot[j], -n);

            // Outer wall (faces outward); inner wall (faces toward the centre).
            Quad(side, outerBot[i], outerBot[j], outerTop[j], outerTop[i],
                Radial(i, j, +1));
            Quad(side, innerBot[j], innerBot[i], innerTop[i], innerTop[j],
                Radial(i, j, -1));
        }

        // End caps: the cross-section quad at each end, facing along ∓ the path tangent.
        var tan0 = new Vector3(1, 0, 0);
        var tanN = new Vector3(Mathf.Cos(arc), 0, -dir * Mathf.Sin(arc));
        Quad(side, innerBot[0], outerBot[0], outerTop[0], innerTop[0], -tan0);
        Quad(side, outerBot[seg], innerBot[seg], innerTop[seg], outerTop[seg], tanN);

        var mesh = new ArrayMesh();
        Commit(surface, mesh);
        Commit(side, mesh);
        return mesh;

        // Outward radial direction averaged across the segment, signed (+1 outer / −1 inner wall normal).
        Vector3 Radial(int i, int j, int sign)
        {
            Vector3 ri = (outerTop[i] - innerTop[i]);
            Vector3 rj = (outerTop[j] - innerTop[j]);
            return (sign * (ri + rj)).Normalized();
        }

        // A right turn (dir<0) is a Z-reflection of the left, which flips quad orientation; reversing the
        // corner order (a,b,c,d → a,d,c,b) restores front faces while keeping the supplied outward normal.
        void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 nrm)
        {
            if (dir >= 0) MeshBuilder.AddQuad(st, a, b, c, d, nrm);
            else MeshBuilder.AddQuad(st, a, d, c, b, nrm);
        }

        void QuadUV(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 nrm,
            Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            if (dir >= 0) MeshBuilder.AddQuad(st, a, b, c, d, nrm, ua, ub, uc, ud);
            else MeshBuilder.AddQuad(st, a, d, c, b, nrm, ua, ud, uc, ub);
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
}
