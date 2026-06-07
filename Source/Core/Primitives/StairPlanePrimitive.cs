using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Geometry;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A staircase with no solid base: a constant-thickness folded plate following the step silhouette —
/// the "thick stepped plane" counterpart to <see cref="StairsPrimitive"/>. The top is the same
/// tread/riser silhouette climbing <c>totalRise</c> over <c>run</c> in <c>steps</c> equal steps; the
/// underside is that silhouette offset by <c>thickness</c> perpendicular to each face, which (because
/// treads are horizontal and risers vertical) is exactly the top profile translated by (+t, −t).
///
/// That translate only yields a clean, non-self-intersecting plate while <c>t &lt; min(tread, riser)</c>,
/// so <see cref="BuildMesh"/> clamps thickness to 95% of that minimum — the static ParamSpec max can't,
/// since tread/riser depend on run/steps. Local space is centred on X (run) and Z (width); X(u)=u-run/2
/// and the flight ascends along local +X. Ends are squared (vertical foot + back), not 45°-bevelled.
/// Surfaces: 0 Tread (steps + the top-landing lip), 1 Riser (step fronts + the foot lip), 2 Side
/// (underside, riser backs, the two stepped side caps and the foot/back fillers).
/// </summary>
public sealed class StairPlanePrimitive : IPrimitive
{
    public string TypeId => "stair_plane";
    public string DisplayName => "Stair Plane";
    public string Category => "Vertical";

    public IReadOnlyList<ParamSpec> Parameters { get; } = new[]
    {
        new ParamSpec("steps",     "Steps",      ParamType.Int,   12,    1f,    100f),
        new ParamSpec("totalRise", "Total Rise", ParamType.Float, 3.0f,  0.05f, 100f),
        new ParamSpec("run",       "Run",        ParamType.Float, 3.0f,  0.1f,  1000f),
        new ParamSpec("width",     "Width",      ParamType.Float, 1.2f,  0.1f,  100f),
        new ParamSpec("thickness", "Thickness",  ParamType.Float, 0.1f,  0.01f, 50f),
    };

    public IReadOnlyList<string> MaterialSlots { get; } = new[] { "Tread", "Riser", "Side" };

    public ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx)
    {
        int n = Mathf.Max(1, GetI(data, "steps", 12));
        float rise = GetF(data, "totalRise", 3f);
        float run = GetF(data, "run", 3f);
        float w = GetF(data, "width", 1.2f);
        float zl = -w * 0.5f, zr = w * 0.5f;
        float tread = run / n, riser = rise / n;
        // Clamp so the (+t, −t) underside never crosses its own step → no self-intersection.
        float t = Mathf.Min(GetF(data, "thickness", 0.1f), 0.95f * Mathf.Min(tread, riser));

        float X(float u) => u - run * 0.5f;

        SurfaceTool treads = Begin(), risers = Begin(), side = Begin();

        // One side-cap quad per profile segment (z=zl outward −Z, z=zr outward +Z). The band slice
        // between a top edge V0→V1 and its (+t,−t)-translated underside edge is a planar parallelogram.
        // The sides lie in the XY plane, so UVs come from each corner's (x,y) position (U along the run,
        // V from the top) — same trick StairsPrimitive uses. AddQuad's default rectangle-from-edge-lengths
        // UVs would skew the texture diagonally across these SHEARED quads (the underside corner is offset
        // along (+t,−t), not square to the top edge). U is mirrored on the −Z face so it reads upright.
        Vector2 UvL(Vector3 p) => new(run * 0.5f - p.X, rise - p.Y); // −Z side: run − u, u = x + run/2
        Vector2 UvR(Vector3 p) => new(run * 0.5f + p.X, rise - p.Y); // +Z side: u
        void SideSegment(Vector3 v0, Vector3 v1)
        {
            Vector3 v0b = v0 + new Vector3(t, -t, 0), v1b = v1 + new Vector3(t, -t, 0);
            MeshBuilder.AddQuad(side,
                new(v0.X, v0.Y, zl), new(v1.X, v1.Y, zl), new(v1b.X, v1b.Y, zl), new(v0b.X, v0b.Y, zl),
                new Vector3(0, 0, -1),
                UvL(v0), UvL(v1), UvL(v1b), UvL(v0b));
            MeshBuilder.AddQuad(side,
                new(v0b.X, v0b.Y, zr), new(v1b.X, v1b.Y, zr), new(v1.X, v1.Y, zr), new(v0.X, v0.Y, zr),
                new Vector3(0, 0, 1),
                UvR(v0b), UvR(v1b), UvR(v1), UvR(v0));
        }

        for (int i = 0; i < n; i++)
        {
            float u0 = i * tread, u1 = (i + 1) * tread;
            float yBot = i * riser, yTop = (i + 1) * riser;
            float x0 = X(u0), x1 = X(u1);
            float xb = x0 + t, ybB = yBot - t, ytB = yTop - t; // underside (back/down) coordinates

            // Riser front (faces −X) and its under/back face (faces +X).
            MeshBuilder.AddQuad(risers,
                new(x0, yBot, zl), new(x0, yBot, zr), new(x0, yTop, zr), new(x0, yTop, zl), new Vector3(-1, 0, 0));
            MeshBuilder.AddQuad(side,
                new(xb, ybB, zl), new(xb, ytB, zl), new(xb, ytB, zr), new(xb, ybB, zr), new Vector3(1, 0, 0));

            // Tread top (faces +Y) and its underside (faces −Y).
            MeshBuilder.AddQuad(treads,
                new(x0, yTop, zl), new(x0, yTop, zr), new(x1, yTop, zr), new(x1, yTop, zl), Vector3.Up);
            MeshBuilder.AddQuad(side,
                new(xb, ytB, zl), new(x1 + t, ytB, zl), new(x1 + t, ytB, zr), new(xb, ytB, zr), Vector3.Down);

            // Side caps: the riser segment then the tread segment of the stepped silhouette.
            SideSegment(new(x0, yBot, 0), new(x0, yTop, 0));
            SideSegment(new(x0, yTop, 0), new(x1, yTop, 0));
        }

        // Square end caps (foot + back landing). The underside is the top profile shifted by (+t,−t),
        // so the foot's top-front (xf,0) and the underside's start (xf+t,−t) differ by that diagonal —
        // connecting them directly is what used to give a 45° bevel. Instead drop a VERTICAL front face
        // down to (xf,−t) plus a flat bottom across to the underside, and fill the triangular gap left on
        // each side cap. The back landing is squared the same way: extend the last tread out to
        // (xt+t,rise) and drop a vertical back to (xt+t,rise−t). The vertical front sits on the Riser
        // surface (flush below the first riser) and the landing lip on Tread; fillers reuse UvL/UvR.
        float xf = X(0f), xt = X(run);

        // Foot: vertical front, flat bottom, then the −Z / +Z side-cap fillers (triangle (xf,0)-(xf+t,−t)-(xf,−t)).
        MeshBuilder.AddQuad(risers,
            new(xf, 0, zl), new(xf, -t, zl), new(xf, -t, zr), new(xf, 0, zr), new Vector3(-1, 0, 0));
        MeshBuilder.AddQuad(side,
            new(xf, -t, zl), new(xf + t, -t, zl), new(xf + t, -t, zr), new(xf, -t, zr), Vector3.Down);
        MeshBuilder.AddTri(side, new(xf, 0, zl), new(xf + t, -t, zl), new(xf, -t, zl), new Vector3(0, 0, -1),
            UvL(new(xf, 0, 0)), UvL(new(xf + t, -t, 0)), UvL(new(xf, -t, 0)));
        MeshBuilder.AddTri(side, new(xf, 0, zr), new(xf, -t, zr), new(xf + t, -t, zr), new Vector3(0, 0, 1),
            UvR(new(xf, 0, 0)), UvR(new(xf, -t, 0)), UvR(new(xf + t, -t, 0)));

        // Back landing: horizontal tread lip, vertical back, then the −Z / +Z side-cap fillers.
        MeshBuilder.AddQuad(treads,
            new(xt, rise, zl), new(xt, rise, zr), new(xt + t, rise, zr), new(xt + t, rise, zl), Vector3.Up);
        MeshBuilder.AddQuad(side,
            new(xt + t, rise - t, zl), new(xt + t, rise, zl), new(xt + t, rise, zr), new(xt + t, rise - t, zr), new Vector3(1, 0, 0));
        MeshBuilder.AddTri(side, new(xt, rise, zl), new(xt + t, rise, zl), new(xt + t, rise - t, zl), new Vector3(0, 0, -1),
            UvL(new(xt, rise, 0)), UvL(new(xt + t, rise, 0)), UvL(new(xt + t, rise - t, 0)));
        MeshBuilder.AddTri(side, new(xt, rise, zr), new(xt + t, rise - t, zr), new(xt + t, rise, zr), new Vector3(0, 0, 1),
            UvR(new(xt, rise, 0)), UvR(new(xt + t, rise - t, 0)), UvR(new(xt + t, rise, 0)));

        var mesh = new ArrayMesh();
        Commit(treads, mesh);
        Commit(risers, mesh);
        Commit(side, mesh);
        return mesh;
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
