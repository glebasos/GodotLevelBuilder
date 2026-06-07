using System.Collections.Generic;
using Godot;

namespace LevelBuilder.Core.Data;

/// <summary>
/// Loads a <see cref="Texture2D"/> from a res:// path, preferring Godot's import pipeline but
/// falling back to a raw on-disk decode when the file hasn't been imported yet.
///
/// This fallback is what makes a JUST-added user texture usable in the same session: a PNG copied
/// into the project at runtime has no <c>.ctex</c> until the editor reimports it, so
/// <see cref="ResourceLoader"/> can't see it — but <see cref="Image.LoadFromFile"/> reads the file
/// straight off disk. Imported textures still take the good path (mipmaps/compression). Cached by path.
/// </summary>
public static class TextureLoader
{
    // Cached by path. Known limitation: re-adding a DIFFERENT file under the same name shows the stale
    // texture this session (MaterialResolver caches by id too — evicting both is the real fix). Restart clears it.
    private static readonly Dictionary<string, Texture2D> _cache = new();

    /// <summary>The texture at <paramref name="path"/>, or null if it can't be loaded or decoded.</summary>
    public static Texture2D Load(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_cache.TryGetValue(path, out Texture2D cached)) return cached;

        Texture2D tex = LoadUncached(path);
        if (tex != null) _cache[path] = tex;
        return tex;
    }

    private static Texture2D LoadUncached(string path)
    {
        // Imported asset: let Godot load the .ctex (mipmaps, compression, the real thing).
        if (ResourceLoader.Exists(path))
        {
            var imported = ResourceLoader.Load<Texture2D>(path);
            if (imported != null) return imported;
        }

        // Not imported yet (freshly copied this session) — decode the raw file directly.
        string abs = ProjectSettings.GlobalizePath(path);
        Image img = Image.LoadFromFile(abs);
        return img == null ? null : ImageTexture.CreateFromImage(img);
    }
}
