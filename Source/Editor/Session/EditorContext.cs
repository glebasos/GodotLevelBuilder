using System.Collections.Generic;
using Godot;
using LevelBuilder.Core;
using LevelBuilder.Core.Build;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Primitives;
using LevelBuilder.Editor.Commands;
using LevelBuilder.Editor.Gizmos;
using LevelBuilder.Editor.Grid;
using LevelBuilder.Editor.View;

namespace LevelBuilder.Editor.Session;

/// <summary>
/// Shared editing state passed to tools: the active document/storey, the primitive
/// registry, the undo stack, the live view, the grid cursor, and a layer to park
/// transient draw previews. The single hub tools talk to.
/// </summary>
public sealed class EditorContext
{
    public LevelDocument Document { get; init; }
    /// <summary>The storey currently being edited; new geometry goes here. Switch with <see cref="StoreyUp"/>/<see cref="StoreyDown"/>.</summary>
    public StoreyData Storey { get; private set; }
    public PrimitiveRegistry Registry { get; init; }
    public CommandStack Commands { get; init; }
    public LevelView View { get; init; }
    public GridCursor Cursor { get; init; }
    public GridRenderer Grid { get; init; }
    public Node3D PreviewLayer { get; init; }
    public InstancePicker Picker { get; init; }
    public GizmoLayer Gizmos { get; init; }

    public string SelectedId { get; private set; }
    /// <summary>Non-null when an opening is selected; <see cref="SelectedId"/> is then its owning wall.</summary>
    public string SelectedOpeningId { get; private set; }

    private IReadOnlyList<IEditHandle> _handles = new List<IEditHandle>();
    /// <summary>Resize handles for the current selection (indexed by the picker's HandleIndex).</summary>
    public IReadOnlyList<IEditHandle> Handles => _handles;

    /// <summary>
    /// Raised whenever the scene changes: any structural edit, selection change, active-storey
    /// switch, or live-drag frame routes through here. UI panels (e.g. the scene tree) subscribe
    /// to stay in sync. Fired often (per drag frame) — subscribers should cheaply gate their work.
    /// </summary>
    public event System.Action Changed;

    /// <summary>
    /// Re-syncs everything derived from selection + document state: the view's selection, the live
    /// mesh, and the gizmo handles. The single choke point for "the scene changed" — every command,
    /// selection change, and live drag frame routes through here, so the handle widgets track edits
    /// as they happen. (A live drag holds its handle in SelectTool, so rebuilding this list doesn't
    /// disturb the in-flight drag.)
    /// </summary>
    public void Refresh()
    {
        View.SetSelection(SelectedId, SelectedOpeningId);
        _handles = BuildHandles();
        View.Rebuild();
        Gizmos.Rebuild(_handles);
        Changed?.Invoke();
    }

    private List<IEditHandle> BuildHandles()
    {
        if (SelectedOpeningId != null)
        {
            (PrimitiveInstanceData wall, OpeningData opening) = FindOpening(SelectedId, SelectedOpeningId);
            return OpeningHandleProvider.Build(wall, opening, OffsetOfInstance(SelectedId));
        }
        if (SelectedId == null) return new List<IEditHandle>();
        PrimitiveInstanceData inst = GetInstance(SelectedId);
        if (inst == null) return new List<IEditHandle>();
        return InstanceHandleProvider.Build(inst, Registry.Get(inst.PrimitiveType), OffsetOfInstance(SelectedId));
    }

    /// <summary>World offset of the ACTIVE storey's floor plane (where new geometry is drawn).</summary>
    public Vector3 ElevationOffset => new(0, Storey.BaseElevation, 0);

    /// <summary>World offset of the storey that OWNS <paramref name="id"/> — not necessarily the active one.</summary>
    public Vector3 OffsetOfInstance(string id)
    {
        (StoreyData s, _, _) = Find(id);
        return new Vector3(0, (s ?? Storey).BaseElevation, 0);
    }

    /// <summary>Offset of the storey owning the current selection, so handles/drags sit on the right level.</summary>
    public Vector3 SelectedInstanceOffset => OffsetOfInstance(SelectedId);

    // ---- storeys ---------------------------------------------------------

    /// <summary>Moves up one storey, creating a new one stacked on top at the frontier.</summary>
    public void StoreyUp() => SwitchStorey(+1);
    /// <summary>Moves down one storey, creating a new one stacked below at the frontier.</summary>
    public void StoreyDown() => SwitchStorey(-1);

    /// <summary>
    /// Makes <paramref name="s"/> the active storey: the grid and snap cursor jump to its elevation and
    /// any selection (possibly on another storey) is cleared. Also the single entry point used at startup.
    /// </summary>
    public void SetActiveStorey(StoreyData s)
    {
        Storey = s;
        Cursor.Elevation = s.BaseElevation;
        if (Grid != null) Grid.Position = new Vector3(0, s.BaseElevation, 0);
        GD.Print($"[storey] active: {s.Name}  (base {s.BaseElevation:0.##} m, height {s.Height:0.##} m)");
        ClearSelection(); // drop a selection from another storey; refreshes view + gizmos if needed
        Changed?.Invoke(); // ensure the active-storey marker updates even when nothing was selected
    }

    private void SwitchStorey(int dir)
    {
        List<StoreyData> sorted = SortedStoreys();
        int idx = sorted.IndexOf(Storey);
        StoreyData target = dir > 0
            ? (idx + 1 < sorted.Count ? sorted[idx + 1] : CreateStacked(sorted[^1], +1))
            : (idx - 1 >= 0 ? sorted[idx - 1] : CreateStacked(sorted[0], -1));
        SetActiveStorey(target);
    }

    private List<StoreyData> SortedStoreys()
    {
        var list = new List<StoreyData>();
        foreach (StoreyData s in Document.Storeys) list.Add(s);
        list.Sort((a, b) => a.BaseElevation.CompareTo(b.BaseElevation));
        return list;
    }

    /// <summary>New storey flush above (dir=+1) or below (dir=-1) <paramref name="from"/>, inheriting its height.</summary>
    private StoreyData CreateStacked(StoreyData from, int dir)
    {
        float height = from.Height;
        float baseElev = dir > 0 ? from.BaseElevation + from.Height : from.BaseElevation - height;
        var s = new StoreyData
        {
            Id = Ids.New(),
            Name = $"Storey {baseElev:0.##} m",
            BaseElevation = baseElev,
            Height = height,
        };
        Document.Storeys.Add(s);
        return s;
    }

    public BuildContext BuildCtx() => new()
    {
        Materials = Document.Materials,
        CellSize = Document.Grid.CellSize,
        StoreyHeight = Storey.Height,
    };

    public void AddInstance(PrimitiveInstanceData instance)
    {
        DefaultMaterials.ApplyDefaults(instance); // give freshly drawn geometry placeholder textures (no picker UI yet)
        Commands.Execute(new AddInstanceCommand(Storey, instance, Refresh));
    }

    public void Undo() => Commands.Undo();
    public void Redo() => Commands.Redo();

    // ---- selection -------------------------------------------------------

    /// <summary>Picks the instance (or opening) under the mouse and selects it (or clears on a miss).</summary>
    public void PickAndSelect()
    {
        PickResult r = Picker.Pick();
        if (!r.Hit) ClearSelection();
        else if (r.IsOpening) SelectOpening(r.InstanceId, r.OpeningId);
        else Select(r.InstanceId);
    }

    public PrimitiveInstanceData GetInstance(string id)
    {
        (_, PrimitiveInstanceData inst, _) = Find(id);
        return inst;
    }

    public void AddOpening(PrimitiveInstanceData wall, OpeningData opening)
        => Commands.Execute(new AddOpeningCommand(wall, opening, Refresh));

    public void Select(string id)
    {
        SelectedId = id;
        SelectedOpeningId = null;
        Refresh();
    }

    /// <summary>Selects an opening: the wall is drawn intact and the opening shows as a solid placeholder.</summary>
    public void SelectOpening(string wallId, string openingId)
    {
        SelectedId = wallId;
        SelectedOpeningId = openingId;
        Refresh();
    }

    public void ClearSelection()
    {
        if (SelectedId == null && SelectedOpeningId == null) return;
        SelectedId = null;
        SelectedOpeningId = null;
        Refresh();
    }

    public void DeleteSelected()
    {
        if (SelectedOpeningId != null)
        {
            (PrimitiveInstanceData wall, OpeningData opening) = FindOpening(SelectedId, SelectedOpeningId);
            if (opening == null) { ClearSelection(); return; }

            SelectedId = null;
            SelectedOpeningId = null; // command's refresh will rebuild without the placeholder
            Commands.Execute(new RemoveOpeningCommand(wall, opening, Refresh));
            return;
        }

        if (SelectedId == null) return;
        (StoreyData storey, PrimitiveInstanceData inst, int index) = Find(SelectedId);
        if (inst == null) { ClearSelection(); return; }

        SelectedId = null; // command's refresh will rebuild without the highlight
        Commands.Execute(new RemoveInstanceCommand(storey, inst, index, Refresh));
    }

    private (PrimitiveInstanceData, OpeningData) FindOpening(string wallId, string openingId)
    {
        PrimitiveInstanceData wall = GetInstance(wallId);
        if (wall == null) return (null, null);
        foreach (OpeningData o in wall.Openings)
            if (o.Id == openingId) return (wall, o);
        return (null, null);
    }

    private (StoreyData, PrimitiveInstanceData, int) Find(string id)
    {
        foreach (StoreyData s in Document.Storeys)
        {
            for (int i = 0; i < s.Instances.Count; i++)
                if (s.Instances[i].Id == id) return (s, s.Instances[i], i);
        }
        return (null, null, -1);
    }

    private const string SourceDir = "res://Saved";
    private const string BakedDir = "res://Baked";

    /// <summary>Saves the editable source .tres (re-openable).</summary>
    public void SaveSource()
    {
        EnsureDir(SourceDir);
        string path = $"{SourceDir}/{FileStem()}.tres";
        Error e = LevelSerializer.Save(Document, path);
        Report("save", path, e);
    }

    /// <summary>Bakes a game-ready .tscn (meshes + collision) you can open in Godot.</summary>
    public void BakeToGodot()
    {
        EnsureDir(BakedDir);
        string path = $"{BakedDir}/{FileStem()}.tscn";
        Error e = new SceneBaker(Registry).BakeToFile(Document, path);
        Report("bake", path, e);
    }

    private string FileStem()
    {
        string raw = string.IsNullOrWhiteSpace(Document.Name) ? "Untitled" : Document.Name;
        return raw.Replace(' ', '_');
    }

    private static void EnsureDir(string dir)
    {
        Error e = DirAccess.MakeDirRecursiveAbsolute(dir);
        if (e != Error.Ok && e != Error.AlreadyExists) GD.PushWarning($"Could not create {dir}: {e}");
    }

    private static void Report(string action, string path, Error e)
    {
        if (e == Error.Ok) GD.Print($"[{action}] wrote {path}");
        else GD.PrintErr($"[{action}] failed ({e}) for {path}");
    }
}
