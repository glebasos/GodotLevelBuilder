using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Geometry;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A gameplay marker (Start, Goal, Bone …): placed, selected, moved, saved and undone like any primitive,
/// but it is NOT level geometry. In the editor it draws a coloured proxy mesh (and a pick box); the baker
/// skips it for meshes/collision and instead emits a <c>Marker3D</c> under <c>Markers/</c> that the game
/// swaps for the real object (see docs/SUPER_CORGI_BALL.md).
///
/// Every marker has a <c>yaw</c> parameter (degrees about +Y; there is no rotate gizmo). Facing follows the
/// Godot convention: yaw 0 looks down local −Z. <see cref="MarkerLocal"/> is the marker's pose relative to
/// the instance's LocalTransform — yaw plus any vertical lift (e.g. a bone floating above the floor).
/// </summary>
public abstract class MarkerPrimitive : IPrimitive
{
    public abstract string TypeId { get; }
    public abstract string DisplayName { get; }
    public string Category => "Gameplay";

    /// <summary>The <c>scb_kind</c> metadata the baked Marker3D carries ("spawn", "goal", "bone").</summary>
    public abstract string MarkerKind { get; }

    public abstract IReadOnlyList<ParamSpec> Parameters { get; }

    /// <summary>No material slots: the proxy colours itself and never reaches the baked scene.</summary>
    public IReadOnlyList<string> MaterialSlots { get; } = System.Array.Empty<string>();

    /// <summary>Proxy colour in the editor viewport.</summary>
    protected abstract Color ProxyColor { get; }

    /// <summary>Adds the proxy's boxes, in marker-local space (yaw/lift are applied by the caller).</summary>
    protected abstract void BuildProxy(SurfaceTool st, PrimitiveInstanceData data);

    /// <summary>Pick-box size + centre in marker-local space (what the editor raycasts against).</summary>
    protected abstract (Vector3 size, Vector3 centre) PickBox(PrimitiveInstanceData data);

    /// <summary>Vertical offset of the marker above the instance origin (0 = sits on the grid plane).</summary>
    protected virtual float Lift(PrimitiveInstanceData data) => 0f;

    /// <summary>Marker pose relative to the instance's LocalTransform: yaw about Y, then lifted.</summary>
    public Transform3D MarkerLocal(PrimitiveInstanceData data)
        => new(new Basis(Vector3.Up, Mathf.DegToRad(GetF(data, "yaw", 0f))), new Vector3(0, Lift(data), 0));

    /// <summary>Gameplay values written onto the baked Marker3D as <c>scb_&lt;key&gt;</c> metadata.</summary>
    public virtual IEnumerable<(string key, Variant value)> BakeMeta(PrimitiveInstanceData data)
    {
        yield break;
    }

    public ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        BuildProxy(st, data);
        st.GenerateTangents();
        var mesh = new ArrayMesh();
        st.Commit(mesh);
        // Transform the proxy into instance space so the editor shows the yaw/lift.
        mesh = Transformed(mesh, MarkerLocal(data));
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            AlbedoColor = ProxyColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
        });
        return mesh;
    }

    public Shape3D[] BuildCollision(PrimitiveInstanceData data, BuildContext ctx)
    {
        (Vector3 size, Vector3 centre) = PickBox(data);
        Transform3D local = MarkerLocal(data);
        var verts = new List<Vector3>();
        foreach (Vector3 v in MeshBuilder.Box(size).GetFaces())
            verts.Add(local * (v + centre));
        return new Shape3D[] { new ConcavePolygonShape3D { Data = verts.ToArray() } };
    }

    /// <summary>Axis-aligned box from its min corner to max corner, all six faces outward.</summary>
    protected static void AddBox(SurfaceTool st, Vector3 min, Vector3 max)
    {
        Vector3 a = min, b = max;
        // Each face: four corners + an outward reference normal; AddQuadFacing fixes the winding.
        MeshBuilder.AddQuadFacing(st, new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z), Vector3.Back);
        MeshBuilder.AddQuadFacing(st, new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z), Vector3.Forward);
        MeshBuilder.AddQuadFacing(st, new(a.X, b.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z), Vector3.Up);
        MeshBuilder.AddQuadFacing(st, new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, a.Y, b.Z), new(a.X, a.Y, b.Z), Vector3.Down);
        MeshBuilder.AddQuadFacing(st, new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z), new(b.X, a.Y, b.Z), Vector3.Right);
        MeshBuilder.AddQuadFacing(st, new(a.X, a.Y, a.Z), new(a.X, b.Y, a.Z), new(a.X, b.Y, b.Z), new(a.X, a.Y, b.Z), Vector3.Left);
    }

    /// <summary>A flat arrow on the ground pointing down local −Z (the marker's facing).</summary>
    protected static void AddFacingArrow(SurfaceTool st, float length, float y)
    {
        const float shaft = 0.12f, head = 0.35f;
        AddBox(st, new Vector3(-shaft, y, -length + head), new Vector3(shaft, y + 0.06f, 0));
        AddBox(st, new Vector3(-head, y, -length), new Vector3(head, y + 0.06f, -length + head));
    }

    private static ArrayMesh Transformed(ArrayMesh source, Transform3D xform)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.AppendFrom(source, 0, xform);
        var mesh = new ArrayMesh();
        st.Commit(mesh);
        return mesh;
    }

    protected static float GetF(PrimitiveInstanceData d, string key, float def)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsSingle() : def;
}
