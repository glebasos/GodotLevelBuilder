using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;

namespace LevelBuilder.Core.Primitives;

/// <summary>
/// A marker that carries real, textured geometry borrowed from an existing primitive (<see cref="Shape"/>:
/// a floor slab for a moving platform, a cylinder for a bumper). The shape reads its own parameter keys
/// straight from the instance, so the marker just declares them alongside its gameplay values.
///
/// Editor: the shape (yaw applied) with its material slots, plus an optional trailing proxy surface
/// (e.g. a platform's travel path) in <see cref="MarkerPrimitive.ProxyColor"/>. Bake: the shape alone, as
/// the marker's <c>Mesh</c> child — the proxy never reaches the game.
/// </summary>
public abstract class GeometryMarkerPrimitive : MarkerPrimitive
{
    /// <summary>The primitive whose mesh this marker bakes.</summary>
    protected abstract IPrimitive Shape { get; }

    public override IReadOnlyList<string> MaterialSlots => Shape.MaterialSlots;

    /// <summary>Whether <see cref="MarkerPrimitive.BuildProxy"/> adds anything for this instance.</summary>
    protected virtual bool HasProxy(PrimitiveInstanceData data) => false;

    protected override void BuildProxy(SurfaceTool st, PrimitiveInstanceData data) { }

    protected override (Vector3 size, Vector3 centre) PickBox(PrimitiveInstanceData data) => (Vector3.Zero, Vector3.Zero);

    public override ArrayMesh BuildBakeMesh(PrimitiveInstanceData data, BuildContext ctx) => Shape.BuildMesh(data, ctx);

    public override ArrayMesh BuildMesh(PrimitiveInstanceData data, BuildContext ctx)
    {
        Transform3D local = MarkerLocal(data);
        ArrayMesh mesh = Transformed(Shape.BuildMesh(data, ctx), local);
        if (!HasProxy(data)) return mesh;

        // Trailing surface (after every material slot), so the positional slot mapping is untouched.
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        BuildProxy(st, data);
        var proxy = new ArrayMesh();
        st.Commit(proxy);
        var moved = new SurfaceTool();
        moved.Begin(Mesh.PrimitiveType.Triangles);
        moved.AppendFrom(proxy, 0, local);
        moved.Commit(mesh);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, new StandardMaterial3D
        {
            AlbedoColor = ProxyColor,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        });
        return mesh;
    }

    public override Shape3D[] BuildCollision(PrimitiveInstanceData data, BuildContext ctx)
        => new Shape3D[] { Transformed(Shape.BuildMesh(data, ctx), MarkerLocal(data)).CreateTrimeshShape() };
}
