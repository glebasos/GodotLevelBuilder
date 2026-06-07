using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Primitives;

namespace LevelBuilder.Core.Build;

/// <summary>
/// Resolves a primitive instance's material slots to real <see cref="Material"/>s and writes
/// them onto the mesh SURFACE (never surface_material_override — that stays free for the
/// consuming game; see docs/EXPORT.md). Shared by the live LevelView and the SceneBaker so
/// the editor preview matches the bake exactly. Loaded materials are cached by id; a missing
/// or unloadable material resolves to null (surface stays default grey, no crash).
/// </summary>
public sealed class MaterialResolver
{
    private readonly Dictionary<string, Material> _cache = new();

    /// <summary>Maps each material slot of <paramref name="prim"/> to a library material on the matching surface.</summary>
    public void AssignSurfaceMaterials(ArrayMesh mesh, IPrimitive prim, PrimitiveInstanceData inst, MaterialLibrary library)
    {
        int surfaces = mesh.GetSurfaceCount();
        for (int i = 0; i < prim.MaterialSlots.Count && i < surfaces; i++)
        {
            string slot = prim.MaterialSlots[i];
            if (!inst.MaterialSlots.ContainsKey(slot)) continue;

            Material mat = Resolve(inst.MaterialSlots[slot].AsString(), library);
            if (mat != null) mesh.SurfaceSetMaterial(i, mat);
        }
    }

    /// <summary>Library id -> Material (loaded from the entry's MaterialPath), cached. Null if unresolved.</summary>
    public Material Resolve(string materialId, MaterialLibrary library)
    {
        if (string.IsNullOrEmpty(materialId)) return null;
        if (_cache.TryGetValue(materialId, out Material cached)) return cached;

        MaterialEntry entry = library?.Find(materialId);
        Material mat = BuildFrom(entry);

        _cache[materialId] = mat;
        return mat;
    }

    /// <summary>An entry's material: a loaded .material/.tres if it has one, else a StandardMaterial3D built from its texture.</summary>
    private static Material BuildFrom(MaterialEntry entry)
    {
        if (entry == null) return null;

        if (!string.IsNullOrEmpty(entry.MaterialPath) && ResourceLoader.Exists(entry.MaterialPath))
            return ResourceLoader.Load<Material>(entry.MaterialPath);

        if (!string.IsNullOrEmpty(entry.TexturePath))
        {
            Texture2D tex = TextureLoader.Load(entry.TexturePath); // imported, or raw-decoded if not yet reimported
            return tex == null ? null : new StandardMaterial3D { AlbedoTexture = tex };
        }

        return null;
    }
}
