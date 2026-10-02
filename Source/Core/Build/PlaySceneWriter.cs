using Godot;

namespace LevelBuilder.Core.Build;

/// <summary>
/// Super Corgi Ball: after an export, writes a tiny playable wrapper scene next to the exported chunk —
/// <c>&lt;Name&gt;_Play.tscn</c>, an inherited instance of the game's level template with
/// <c>PivotController/Stage.Level</c> pointing at the chunk — so the level runs with F6 straight away.
///
/// Written as text (the template lives in another project, so it can't be loaded/packed here). Only
/// written when missing: once it exists it belongs to the game (lighting tweaks, LevelSwitcher entry…)
/// and its content wouldn't change anyway, because it only references the chunk by path.
/// See docs/SUPER_CORGI_BALL.md.
/// </summary>
public static class PlaySceneWriter
{
    public const string TemplateResPath = "res://Scenes/Levels/BuilderLevelTemplate.tscn";

    public enum Result { Written, AlreadyExists, NoGameProject, NoTemplate, Failed }

    /// <summary>Writes the wrapper for the chunk at <paramref name="chunkPath"/> (absolute OS path).</summary>
    public static Result WriteFor(string chunkPath, out string playPath)
    {
        playPath = $"{chunkPath.GetBaseDir()}/{chunkPath.GetFile().GetBaseName()}_Play.tscn";

        string root = FindProjectRoot(chunkPath.GetBaseDir());
        if (root == null) return Result.NoGameProject;
        if (!FileAccess.FileExists($"{root}/{TemplateResPath["res://".Length..]}")) return Result.NoTemplate;
        if (FileAccess.FileExists(playPath)) return Result.AlreadyExists;

        string chunkRes = ToResPath(root, chunkPath);
        string nodeName = playPath.GetFile().GetBaseName();
        string text =
$@"[gd_scene format=3]

[ext_resource type=""PackedScene"" path=""{TemplateResPath}"" id=""1_template""]
[ext_resource type=""PackedScene"" path=""{chunkRes}"" id=""2_chunk""]

[node name=""{nodeName}"" instance=ExtResource(""1_template"")]

[node name=""Stage"" parent=""PivotController""]
Level = ExtResource(""2_chunk"")
";
        using FileAccess f = FileAccess.Open(playPath, FileAccess.ModeFlags.Write);
        if (f == null) return Result.Failed;
        f.StoreString(text);
        return Result.Written;
    }

    /// <summary>Walks up from <paramref name="dir"/> to the folder holding <c>project.godot</c>, or null.</summary>
    public static string FindProjectRoot(string dir)
    {
        string d = dir.Replace('\\', '/').TrimEnd('/');
        while (!string.IsNullOrEmpty(d))
        {
            if (FileAccess.FileExists($"{d}/project.godot")) return d;
            string parent = d.GetBaseDir();
            if (parent == d || string.IsNullOrEmpty(parent)) break;
            d = parent;
        }
        return null;
    }

    /// <summary>Absolute path under <paramref name="root"/> → <c>res://…</c>.</summary>
    public static string ToResPath(string root, string absolute)
        => "res://" + absolute.Replace('\\', '/')[(root.Length + 1)..];
}
