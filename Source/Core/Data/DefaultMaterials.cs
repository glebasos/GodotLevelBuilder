using System.Collections.Generic;

namespace LevelBuilder.Core.Data;

/// <summary>
/// Placeholder material palette (Kenney prototype textures) plus the default slot→material
/// assignment for each primitive type. Seeded into a new level's <see cref="MaterialLibrary"/>
/// and used to pre-fill the material slots of freshly drawn instances, so geometry shows a
/// texture without a picker UI yet. A real material picker (and per-instance overrides) come later.
/// </summary>
public static class DefaultMaterials
{
    public const string Floor = "proto_floor";
    public const string Wall = "proto_wall";
    public const string Trim = "proto_trim";

    /// <summary>Adds the placeholder material entries to <paramref name="library"/> (idempotent by id).</summary>
    public static void Seed(MaterialLibrary library)
    {
        Add(library, Floor, "Proto Floor", "res://Assets/Materials/proto_floor.tres");
        Add(library, Wall, "Proto Wall", "res://Assets/Materials/proto_wall.tres");
        Add(library, Trim, "Proto Trim", "res://Assets/Materials/proto_trim.tres");
    }

    /// <summary>Default material id per slot for a primitive type. Empty when none is defined.</summary>
    public static IReadOnlyDictionary<string, string> SlotsFor(string primitiveType) => primitiveType switch
    {
        "floor" => new Dictionary<string, string> { ["Top"] = Floor, ["Bottom"] = Trim, ["Edge"] = Trim },
        "polygon_floor" => new Dictionary<string, string> { ["Top"] = Floor, ["Bottom"] = Trim, ["Edge"] = Trim, ["Rail"] = Wall },
        "circle_plane" => new Dictionary<string, string> { ["Top"] = Floor, ["Bottom"] = Trim, ["Edge"] = Trim, ["Rail"] = Wall },
        "half_circle" => new Dictionary<string, string> { ["Top"] = Floor, ["Bottom"] = Trim, ["Edge"] = Trim, ["Rail"] = Wall },
        "wall" => new Dictionary<string, string>
        {
            ["Front"] = Wall, ["Back"] = Wall, ["Top"] = Trim, ["Ends"] = Trim, ["Reveal"] = Trim,
        },
        "ramp" => new Dictionary<string, string> { ["Surface"] = Floor, ["Side"] = Trim },
        "ramp_plane" => new Dictionary<string, string> { ["Surface"] = Floor, ["Side"] = Trim },
        "banked_curve" => new Dictionary<string, string> { ["Surface"] = Floor, ["Side"] = Trim },
        "half_pipe" => new Dictionary<string, string> { ["Surface"] = Floor, ["Side"] = Trim },
        "edge_curb" => new Dictionary<string, string> { ["Side"] = Wall, ["Top"] = Trim, ["Bottom"] = Trim },
        "cylinder" => new Dictionary<string, string> { ["Side"] = Wall, ["Top"] = Floor, ["Bottom"] = Trim },
        "curved_wall" => new Dictionary<string, string>
        {
            ["Front"] = Wall, ["Back"] = Wall, ["Top"] = Trim, ["Ends"] = Trim,
        },
        "dome" => new Dictionary<string, string> { ["Surface"] = Floor, ["Bottom"] = Trim, ["Side"] = Wall },
        "path_sweep" => new Dictionary<string, string> { ["Surface"] = Floor, ["Side"] = Trim },
        "platform" => new Dictionary<string, string> { ["Top"] = Floor, ["Bottom"] = Trim, ["Edge"] = Trim },
        "bumper" => new Dictionary<string, string> { ["Side"] = Trim, ["Top"] = Wall, ["Bottom"] = Trim },
        _ => new Dictionary<string, string>(),
    };

    /// <summary>Fills any material slot not already set on <paramref name="inst"/> with its primitive default.</summary>
    public static void ApplyDefaults(PrimitiveInstanceData inst)
    {
        foreach (KeyValuePair<string, string> kv in SlotsFor(inst.PrimitiveType))
            if (!inst.MaterialSlots.ContainsKey(kv.Key))
                inst.MaterialSlots[kv.Key] = kv.Value;
    }

    private static void Add(MaterialLibrary library, string id, string displayName, string path)
    {
        if (library.Find(id) != null) return;
        library.Entries.Add(new MaterialEntry { Id = id, DisplayName = displayName, MaterialPath = path });
    }
}
