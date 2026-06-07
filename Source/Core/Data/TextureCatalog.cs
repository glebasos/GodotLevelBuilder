using System.Collections.Generic;
using Godot;

namespace LevelBuilder.Core.Data;

/// <summary>One pickable texture from the asset pool: a res:// path plus how to group/label it.</summary>
public readonly record struct TextureItem(string Path, string Group, string Name);

/// <summary>
/// Discovers the raw textures the builder can paint with — the bundled Kenney prototype pack
/// (res://Assets/kenney_prototype_textures/&lt;color&gt;/texture_NN.png) plus any the user has added
/// (res://Assets/user_textures/*) — and turns a chosen texture into a stable
/// <see cref="MaterialLibrary"/> entry so instances can reference it by id.
/// </summary>
public static class TextureCatalog
{
    public const string Root = "res://Assets/kenney_prototype_textures";

    /// <summary>Where user-added textures are copied so they get a stable res:// path (bake/save reference paths).</summary>
    public const string UserRoot = "res://Assets/user_textures";

    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga" };

    /// <summary>All known textures: the bundled pack (grouped by color) then user-added (grouped "custom").</summary>
    public static List<TextureItem> Load()
    {
        var items = new List<TextureItem>();
        LoadPack(items);
        LoadUser(items);
        return items;
    }

    private static void LoadPack(List<TextureItem> items)
    {
        using DirAccess dir = DirAccess.Open(Root);
        if (dir == null) return;

        foreach (string sub in dir.GetDirectories())
        {
            using DirAccess colorDir = DirAccess.Open($"{Root}/{sub}");
            if (colorDir == null) continue;
            foreach (string file in colorDir.GetFiles())
                if (IsImage(file))
                    items.Add(new TextureItem($"{Root}/{sub}/{file}", sub, file));
        }
    }

    private static void LoadUser(List<TextureItem> items)
    {
        using DirAccess dir = DirAccess.Open(UserRoot);
        if (dir == null) return; // folder only exists once the user has added something

        foreach (string file in dir.GetFiles())
            if (IsImage(file))
                items.Add(new TextureItem($"{UserRoot}/{file}", "custom", file));
    }

    private static bool IsImage(string file)
    {
        string lower = file.ToLowerInvariant();
        foreach (string ext in ImageExts)
            if (lower.EndsWith(ext)) return true;
        return false;
    }

    /// <summary>
    /// Copies a user-chosen image (an OS-absolute path from the file dialog) into <see cref="UserRoot"/>
    /// so it gets a stable res:// path that save/bake can reference. Returns the res:// destination, or
    /// "" on failure. Overwrites an existing file of the same name (re-adding the same texture is idempotent).
    /// </summary>
    public static string ImportUserTexture(string sourcePath)
    {
        Error mk = DirAccess.MakeDirRecursiveAbsolute(UserRoot);
        if (mk != Error.Ok && mk != Error.AlreadyExists)
        {
            GD.PushWarning($"[texture] could not create {UserRoot}: {mk}");
            return "";
        }

        string dest = $"{UserRoot}/{sourcePath.GetFile()}";
        Error e = DirAccess.CopyAbsolute(sourcePath, dest);
        if (e != Error.Ok)
        {
            GD.PrintErr($"[texture] copy failed ({e}): {sourcePath}");
            return "";
        }
        GD.Print($"[texture] added {dest}");
        return dest;
    }

    /// <summary>Stable library id for a texture path (so re-applying the same texture reuses one entry).</summary>
    public static string IdFor(string texturePath) => $"tex:{texturePath}";

    /// <summary>
    /// Ensures <paramref name="library"/> has an entry for <paramref name="texturePath"/> and returns its id.
    /// Idempotent: the same texture always maps to the same entry.
    /// </summary>
    public static string EnsureEntry(MaterialLibrary library, string texturePath)
    {
        string id = IdFor(texturePath);
        if (library.Find(id) == null)
            library.Entries.Add(new MaterialEntry
            {
                Id = id,
                DisplayName = texturePath.GetFile(), // e.g. "texture_03.png"
                TexturePath = texturePath,
            });
        return id;
    }
}
