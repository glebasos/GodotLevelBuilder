using Godot;

namespace LevelBuilder.Core.Data;

/// <summary>
/// The current workspace: the user-chosen folder where editable levels (<c>levels/</c>) and custom
/// textures (<c>textures/</c>) live on disk. A process-wide pointer so the static texture helpers
/// (<see cref="TextureCatalog"/>/<see cref="TextureLoader"/>) and the editor can resolve paths
/// without threading the choice through every call. The persistent pointer lives in AppConfig
/// (<c>user://levelbuilder.cfg</c>); this just holds the resolved absolute root for the session.
///
/// Why a workspace folder (not <c>res://</c>): once the builder is exported as a standalone binary,
/// <c>res://</c> is read-only, so saved levels and added textures cannot live there.
/// </summary>
public static class Workspace
{
    /// <summary>Absolute OS path to the workspace root, or "" if not chosen yet. Forward slashes, no trailing slash.</summary>
    public static string Root { get; private set; } = "";

    public static bool IsSet => !string.IsNullOrEmpty(Root);
    public static string LevelsDir => IsSet ? $"{Root}/levels" : "";
    public static string TexturesDir => IsSet ? $"{Root}/textures" : "";
    /// <summary>Where the local "bake" buttons drop game-ready .tscn output. Writable (unlike
    /// <c>res://</c>, which is read-only once the builder is an exported standalone binary).</summary>
    public static string BakedDir => IsSet ? $"{Root}/baked" : "";
    /// <summary>Where shareable player levels (<c>.scblevel</c>, Super Corgi Ball) are written.</summary>
    public static string SharedDir => IsSet ? $"{Root}/shared" : "";

    /// <summary>Sets the workspace root (normalising slashes) and creates its subfolders.</summary>
    public static void SetRoot(string absPath)
    {
        Root = string.IsNullOrEmpty(absPath) ? "" : absPath.Replace('\\', '/').TrimEnd('/');
        EnsureDirs();
    }

    public static void EnsureDirs()
    {
        if (!IsSet) return;
        DirAccess.MakeDirRecursiveAbsolute(LevelsDir);
        DirAccess.MakeDirRecursiveAbsolute(TexturesDir);
    }

    /// <summary>
    /// The single arbiter for the 3-way texture-path form stored in <see cref="MaterialEntry"/>:
    /// <c>res://…</c> (bundled pack) stays as-is; an absolute OS path stays as-is; anything else is a
    /// workspace-relative path (e.g. <c>textures/foo.png</c>) resolved against the current root.
    /// Used by <see cref="TextureLoader"/> so a level survives the workspace folder being moved.
    /// </summary>
    public static string ResolveTexture(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (stored.StartsWith("res://") || stored.StartsWith("user://")) return stored;
        if (System.IO.Path.IsPathRooted(stored)) return stored;
        return IsSet ? $"{Root}/{stored}" : stored;
    }

    /// <summary>If <paramref name="absPath"/> sits inside the workspace, returns it workspace-relative
    /// (the portable form to store in a .tres); otherwise returns it unchanged.</summary>
    public static string Relativize(string absPath)
    {
        if (!IsSet || string.IsNullOrEmpty(absPath)) return absPath;
        string norm = absPath.Replace('\\', '/');
        string prefix = Root + "/";
        return norm.StartsWith(prefix) ? norm[prefix.Length..] : absPath;
    }
}
