using System.Collections.Generic;
using System.Linq;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Primitives;

namespace LevelBuilder.Core.Build;

/// <summary>
/// Bakes a LevelDocument into a runtime Node3D tree (and a PackedScene/.tscn).
/// Node names are derived from stable IDs so consumer material overrides survive
/// rebake. Library materials are written onto the mesh SURFACE, leaving
/// surface_material_override free for the consuming game. See docs/EXPORT.md.
///
/// Two outputs:
///   <see cref="Bake"/>       — one MeshInstance3D per primitive instance (Wall_&lt;id&gt;…),
///                              inspectable, per-instance material overrides.
///   <see cref="BakeMerged"/> — geometry merged by material into one MeshInstance3D each
///                              (Mesh_&lt;materialId&gt;) + one precise trimesh body: the
///                              fewest-draw-calls "chunk" export.
/// </summary>
public sealed class SceneBaker
{
    private readonly PrimitiveRegistry _registry;

    public SceneBaker(PrimitiveRegistry registry) => _registry = registry;

    /// <summary>Builds the node tree. Caller owns the returned root (free or add to a tree).
    /// <paramref name="embedTextures"/> makes materials self-contained (inline textures) for export
    /// into a different project (see <see cref="MaterialResolver.ResolveEmbedded"/>).</summary>
    public Node3D Bake(LevelDocument doc, bool embedTextures = false)
    {
        var ctx = new BuildContext { Materials = doc.Materials, CellSize = doc.Grid.CellSize };
        var materials = new MaterialResolver();

        var root = new Node3D { Name = SanitizeName(doc.Name) };
        float minY = float.PositiveInfinity;

        foreach (StoreyData storey in doc.Storeys)
        {
            var storeyNode = new Node3D
            {
                Name = $"Storey_{storey.Id}",
                Position = new Vector3(0, storey.BaseElevation, 0),
            };
            root.AddChild(storeyNode);

            var body = new StaticBody3D { Name = $"Collision_{storey.Id}" };
            storeyNode.AddChild(body);

            ctx = new BuildContext
            {
                Materials = doc.Materials,
                CellSize = doc.Grid.CellSize,
                StoreyHeight = storey.Height,
            };

            // Deterministic emission order regardless of in-memory ordering.
            foreach (PrimitiveInstanceData inst in storey.Instances.OrderBy(i => i.Id))
            {
                IPrimitive prim = _registry.Get(inst.PrimitiveType);
                if (prim == null)
                {
                    GD.PushWarning($"SceneBaker: unknown primitive '{inst.PrimitiveType}' (instance {inst.Id}) — skipped.");
                    continue;
                }
                if (prim is MarkerPrimitive) continue; // gameplay markers bake as Marker3D below, not geometry

                ArrayMesh mesh = prim.BuildMesh(inst, ctx);
                materials.AssignSurfaceMaterials(mesh, prim, inst, doc.Materials, embedTextures);
                minY = Mathf.Min(minY, MinY(mesh, storeyNode.Position, inst.LocalTransform));

                // Prefix with the primitive type (Floor_, Wall_, …) so the baked tree is readable
                // instead of an opaque Mesh_<id>. The <id> stays in the name to keep it unique,
                // stable, and deterministic across rebake — that's what lets consumer material
                // overrides survive a rebake (docs/EXPORT.md, the type never changes for an instance).
                string typeName = TypeName(inst.PrimitiveType);

                var mi = new MeshInstance3D
                {
                    Name = $"{typeName}_{inst.Id}",
                    Mesh = mesh,
                    Transform = inst.LocalTransform,
                };
                storeyNode.AddChild(mi);

                Shape3D[] shapes = prim.BuildCollision(inst, ctx);
                for (int s = 0; s < shapes.Length; s++)
                {
                    var cs = new CollisionShape3D
                    {
                        Name = $"{typeName}Shape_{inst.Id}_{s}",
                        Shape = shapes[s],
                        Transform = inst.LocalTransform,
                    };
                    body.AddChild(cs);
                }
            }
        }

        AddMarkers(root, doc, minY);
        return root;
    }

    /// <summary>Bakes and writes a .tscn to <paramref name="path"/>. Returns the save Error.</summary>
    public Error BakeToFile(LevelDocument doc, string path, bool embedTextures = false)
    {
        Node3D root = Bake(doc, embedTextures);
        SetOwnerRecursive(root, root);

        var packed = new PackedScene();
        Error packErr = packed.Pack(root);
        if (packErr != Error.Ok)
        {
            root.QueueFree();
            return packErr;
        }

        Error saveErr = ResourceIo.SaveTo(packed, path); // handles external (target-project) paths
        root.QueueFree();
        return saveErr;
    }

    /// <summary>
    /// Bakes a single merged "chunk": all geometry across every storey is flattened and
    /// grouped BY MATERIAL into one <see cref="MeshInstance3D"/> per material (named
    /// <c>Mesh_&lt;materialId&gt;</c> per docs/EXPORT.md), cutting draw calls to one-per-material.
    /// One precise trimesh (<see cref="ConcavePolygonShape3D"/>) built from the merged visual
    /// geometry gives exact concave collision (the ball rolls inside half-pipes/bowls, openings
    /// stay as real holes) under a single StaticBody3D.
    ///
    /// This is a SEPARATE output from <see cref="Bake"/> (per-instance). The trade-off: merging
    /// collapses per-INSTANCE material overrides into per-MATERIAL — two walls sharing a texture
    /// can no longer be overridden separately in-game. That's the cost of the chunk approach.
    /// Caller owns the returned root.
    /// </summary>
    public Node3D BakeMerged(LevelDocument doc, bool embedTextures = false)
    {
        var materials = new MaterialResolver();
        var root = new Node3D { Name = SanitizeName(doc.Name) };

        // Accumulate every surface, transformed into level-local space, into one SurfaceTool per
        // material id. "" = surfaces with no resolved material (kept as a default-grey group).
        var groups = new Dictionary<string, SurfaceTool>();

        foreach (StoreyData storey in doc.Storeys)
        {
            var ctx = new BuildContext
            {
                Materials = doc.Materials,
                CellSize = doc.Grid.CellSize,
                StoreyHeight = storey.Height,
            };
            // Storeys are flattened: bake the storey elevation into each instance transform.
            var storeyXform = new Transform3D(Basis.Identity, new Vector3(0, storey.BaseElevation, 0));

            foreach (PrimitiveInstanceData inst in storey.Instances.OrderBy(i => i.Id))
            {
                IPrimitive prim = _registry.Get(inst.PrimitiveType);
                if (prim == null)
                {
                    GD.PushWarning($"SceneBaker: unknown primitive '{inst.PrimitiveType}' (instance {inst.Id}) — skipped.");
                    continue;
                }

                if (prim is MarkerPrimitive) continue; // gameplay markers bake as Marker3D below, not geometry

                ArrayMesh mesh = prim.BuildMesh(inst, ctx);
                Transform3D world = storeyXform * inst.LocalTransform;

                int surfaces = mesh.GetSurfaceCount();
                for (int i = 0; i < surfaces && i < prim.MaterialSlots.Count; i++)
                {
                    string slot = prim.MaterialSlots[i];
                    string matId = inst.MaterialSlots.TryGetValue(slot, out Variant v) ? v.AsString() : "";

                    if (!groups.TryGetValue(matId, out SurfaceTool st))
                    {
                        st = new SurfaceTool();
                        st.Begin(Mesh.PrimitiveType.Triangles);
                        groups[matId] = st;
                    }
                    // AppendFrom transforms positions AND normals/tangents by the basis, so the
                    // primitives' deliberate flat per-quad normals arrive correct. Do NOT call
                    // GenerateNormals/Tangents after — that would smooth-shade everything.
                    st.AppendFrom(mesh, i, world);
                }
            }
        }

        // Emit one MeshInstance3D per material (deterministic order), collecting collision faces.
        var collisionFaces = new List<Vector3>();
        foreach (KeyValuePair<string, SurfaceTool> kv in groups.OrderBy(g => g.Key))
        {
            ArrayMesh merged = kv.Value.Commit();
            if (merged.GetSurfaceCount() == 0) continue;

            Material mat = embedTextures
                ? materials.ResolveEmbedded(kv.Key, doc.Materials)
                : materials.Resolve(kv.Key, doc.Materials);
            if (mat != null) merged.SurfaceSetMaterial(0, mat); // on the SURFACE, override stays free

            string name = string.IsNullOrEmpty(kv.Key) ? "Mesh_nomat" : $"Mesh_{SanitizeName(kv.Key)}";
            root.AddChild(new MeshInstance3D { Name = name, Mesh = merged });

            // GetFaces returns level-local triangle verts (the transform is already baked in).
            collisionFaces.AddRange(merged.GetFaces());
        }

        if (collisionFaces.Count > 0)
        {
            var body = new StaticBody3D { Name = "Collision" };
            var shape = new ConcavePolygonShape3D { Data = collisionFaces.ToArray() };
            body.AddChild(new CollisionShape3D { Name = "Trimesh", Shape = shape });
            root.AddChild(body);
        }

        float minY = float.PositiveInfinity;
        foreach (Vector3 v in collisionFaces) minY = Mathf.Min(minY, v.Y);
        AddMarkers(root, doc, minY);
        return root;
    }

    /// <summary>
    /// Emits the gameplay markers (Super Corgi Ball) under a <c>Markers</c> node: one <see cref="Marker3D"/>
    /// per marker instance, named <c>{Type}_{id}</c> (stable, like geometry), tagged <c>scb_kind</c> plus the
    /// marker's own <c>scb_*</c> values. Level-wide settings ride on the <c>Markers</c> node itself:
    /// <c>scb_time_limit</c> (s, 0 = game default) and <c>scb_fall_out_y</c> (level-local Y below which the
    /// ball has fallen off: the lowest geometry minus the Start's fall-out depth). Omitted when the level
    /// has no markers, so plain geometry bakes are unchanged. See docs/SUPER_CORGI_BALL.md.
    /// </summary>
    private void AddMarkers(Node3D root, LevelDocument doc, float geometryMinY)
    {
        var markers = new Node3D { Name = "Markers" };
        int spawns = 0;
        float timeLimit = 0f, fallDepth = 10f;

        foreach (StoreyData storey in doc.Storeys)
        {
            var storeyXform = new Transform3D(Basis.Identity, new Vector3(0, storey.BaseElevation, 0));
            foreach (PrimitiveInstanceData inst in storey.Instances.OrderBy(i => i.Id))
            {
                if (_registry.Get(inst.PrimitiveType) is not MarkerPrimitive marker) continue;

                var node = new Marker3D
                {
                    Name = $"{TypeName(inst.PrimitiveType)}_{inst.Id}",
                    Transform = storeyXform * inst.LocalTransform * marker.MarkerLocal(inst),
                };
                node.SetMeta("scb_kind", marker.MarkerKind);
                foreach ((string key, Variant value) in marker.BakeMeta(inst))
                    node.SetMeta($"scb_{key}", value);
                markers.AddChild(node);

                if (marker is SpawnMarkerPrimitive)
                {
                    spawns++;
                    timeLimit = GetF(inst, "timeLimit", 60f);
                    fallDepth = GetF(inst, "fallDepth", 10f);
                }
            }
        }

        if (markers.GetChildCount() == 0)
        {
            markers.Free();
            return;
        }

        if (spawns != 1)
            GD.PushWarning($"SceneBaker: level '{doc.Name}' has {spawns} Start markers — it needs exactly one.");

        float baseY = float.IsPositiveInfinity(geometryMinY) ? 0f : geometryMinY;
        markers.SetMeta("scb_time_limit", timeLimit);
        markers.SetMeta("scb_fall_out_y", baseY - fallDepth);
        root.AddChild(markers);
    }

    /// <summary>Lowest Y of a mesh's AABB corners once placed (storey offset + instance transform).</summary>
    private static float MinY(ArrayMesh mesh, Vector3 storeyOffset, Transform3D local)
    {
        Aabb box = mesh.GetAabb();
        float min = float.PositiveInfinity;
        for (int i = 0; i < 8; i++)
            min = Mathf.Min(min, (local * box.GetEndpoint(i)).Y + storeyOffset.Y);
        return min;
    }

    private static float GetF(PrimitiveInstanceData d, string key, float def)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsSingle() : def;

    /// <summary>Merge-bakes and writes a .tscn to <paramref name="path"/>. Returns the save Error.</summary>
    public Error BakeMergedToFile(LevelDocument doc, string path, bool embedTextures = false)
    {
        Node3D root = BakeMerged(doc, embedTextures);
        SetOwnerRecursive(root, root);

        var packed = new PackedScene();
        Error packErr = packed.Pack(root);
        if (packErr != Error.Ok)
        {
            root.QueueFree();
            return packErr;
        }

        Error saveErr = ResourceIo.SaveTo(packed, path); // handles external (target-project) paths
        root.QueueFree();
        return saveErr;
    }

    private static void SetOwnerRecursive(Node node, Node owner)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = owner;
            SetOwnerRecursive(child, owner);
        }
    }

    /// <summary>"wall" → "Wall", "floor" → "Floor". Falls back to "Mesh" for a blank type.
    /// Used as the node-name prefix so the baked tree reads by type, not an opaque Mesh_&lt;id&gt;.</summary>
    private static string TypeName(string primitiveType)
        => string.IsNullOrEmpty(primitiveType)
            ? "Mesh"
            : char.ToUpperInvariant(primitiveType[0]) + primitiveType[1..];

    /// <summary>Godot node names disallow . : @ / %. Keep IDs/ASCII; replace the rest.</summary>
    private static string SanitizeName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Level";
        var chars = raw.Select(c => ".:@/%".IndexOf(c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }
}
