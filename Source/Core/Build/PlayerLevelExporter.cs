using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Primitives;

namespace LevelBuilder.Core.Build;

/// <summary>
/// Super Corgi Ball: writes a shareable <c>.scblevel</c> — the format players' levels travel in. Unlike a
/// .tscn/.tres (which can carry scripts and run code when loaded), it is pure data the game parses:
///
///   level.json — { format, version, name, time_limit, fall_out_y, markers: [ {name, kind, transform[12], meta{}} ] }
///   level.glb  — glTF binary: "Geometry" (the merged, textured level meshes) + "Pieces" (one mesh per
///                geometry-carrying marker, named like the marker, in marker-local space).
///
/// Both inside one zip. The game rebuilds the same chunk shape the .tscn export has (meshes + trimesh
/// collision + Markers), so the gameplay side is shared. See docs/SUPER_CORGI_BALL.md.
/// </summary>
public static class PlayerLevelExporter
{
    public const string Extension = "scblevel";
    public const int FormatVersion = 1;

    public static Error Export(LevelDocument doc, PrimitiveRegistry registry, string path)
    {
        Node3D baked = new SceneBaker(registry).BakeMerged(doc, embedTextures: true);
        Node3D gltfRoot = BuildGltfScene(baked, out Dictionary json);
        json["name"] = doc.Name;

        Error e = WriteGlb(gltfRoot, out byte[] glb);
        gltfRoot.Free();
        baked.Free();
        if (e != Error.Ok) return e;

        return WriteZip(path, Json.Stringify(json, "  ").ToUtf8Buffer(), glb);
    }

    /// <summary>Splits the merged bake into the glTF scene (geometry + piece meshes) and the JSON markers.</summary>
    private static Node3D BuildGltfScene(Node3D baked, out Dictionary json)
    {
        var root = new Node3D { Name = "SCBLevel" };
        var geometry = new Node3D { Name = "Geometry" };
        var pieces = new Node3D { Name = "Pieces" };
        root.AddChild(geometry);
        root.AddChild(pieces);

        foreach (MeshInstance3D mesh in baked.GetChildren().OfType<MeshInstance3D>().ToList())
        {
            baked.RemoveChild(mesh);
            geometry.AddChild(mesh);
        }

        json = new Dictionary
        {
            ["format"] = "scblevel",
            ["version"] = FormatVersion,
            ["time_limit"] = 0.0,
            ["fall_out_y"] = -20.0,
        };
        var markers = new Array();
        var markersNode = baked.GetNodeOrNull<Node3D>("Markers");
        if (markersNode != null)
        {
            if (markersNode.HasMeta("scb_time_limit")) json["time_limit"] = markersNode.GetMeta("scb_time_limit");
            if (markersNode.HasMeta("scb_fall_out_y")) json["fall_out_y"] = markersNode.GetMeta("scb_fall_out_y");

            foreach (Node3D marker in markersNode.GetChildren().OfType<Node3D>())
            {
                var meta = new Dictionary();
                foreach (StringName key in marker.GetMetaList())
                {
                    string k = key.ToString();
                    if (k == "scb_kind" || !k.StartsWith("scb_")) continue;
                    meta[k["scb_".Length..]] = ToJson(marker.GetMeta(key));
                }
                markers.Add(new Dictionary
                {
                    ["name"] = marker.Name.ToString(),
                    ["kind"] = marker.GetMeta("scb_kind", "").AsString(),
                    ["transform"] = ToJson(marker.Transform),
                    ["meta"] = meta,
                });

                var pieceMesh = marker.GetNodeOrNull<MeshInstance3D>("Mesh");
                if (pieceMesh != null)
                {
                    marker.RemoveChild(pieceMesh);
                    pieceMesh.Name = marker.Name;
                    pieces.AddChild(pieceMesh);
                }
            }
        }
        json["markers"] = markers;
        return root;
    }

    private static Error WriteGlb(Node3D root, out byte[] glb)
    {
        glb = null;
        // The glTF exporter walks a live tree; park the scene under the root briefly.
        var tree = Engine.GetMainLoop() as SceneTree;
        tree?.Root.AddChild(root);
        var gltf = new GltfDocument();
        var state = new GltfState();
        Error e = gltf.AppendFromScene(root, state);
        if (e == Error.Ok) glb = gltf.GenerateBuffer(state);
        tree?.Root.RemoveChild(root);
        if (e != Error.Ok) return e;
        return glb == null || glb.Length == 0 ? Error.Failed : Error.Ok;
    }

    private static Error WriteZip(string path, byte[] json, byte[] glb)
    {
        var zip = new ZipPacker();
        Error e = zip.Open(path);
        if (e != Error.Ok) return e;
        foreach ((string name, byte[] data) in new[] { ("level.json", json), ("level.glb", glb) })
        {
            zip.StartFile(name);
            zip.WriteFile(data);
            zip.CloseFile();
        }
        return zip.Close();
    }

    private static Variant ToJson(Variant v) => v.VariantType switch
    {
        Variant.Type.Vector3 => new Array { v.AsVector3().X, v.AsVector3().Y, v.AsVector3().Z },
        Variant.Type.Vector2 => new Array { v.AsVector2().X, v.AsVector2().Y },
        Variant.Type.Transform3D => TransformArray(v.AsTransform3D()),
        _ => v,
    };

    /// <summary>Column-major basis (X, Y, Z axes) then origin: 12 floats.</summary>
    private static Array TransformArray(Transform3D t) => new()
    {
        t.Basis.Column0.X, t.Basis.Column0.Y, t.Basis.Column0.Z,
        t.Basis.Column1.X, t.Basis.Column1.Y, t.Basis.Column1.Z,
        t.Basis.Column2.X, t.Basis.Column2.Y, t.Basis.Column2.Z,
        t.Origin.X, t.Origin.Y, t.Origin.Z,
    };
}
