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
    /// <summary>The open document. Swapped in place by <see cref="ReplaceDocument"/> (New/Open).</summary>
    public LevelDocument Document { get; private set; }

    private float _drawHeight;
    /// <summary>
    /// The free absolute elevation of the draw plane, metres — the single vertical truth. Set by the
    /// height indicator (fine) or <see cref="StoreyUp"/>/<see cref="StoreyDown"/> (layer-to-layer).
    /// New geometry lands in the elevation layer at this height (created lazily on placement).
    /// </summary>
    public float DrawHeight => _drawHeight;

    /// <summary>
    /// The elevation layer at the current <see cref="DrawHeight"/>, or null when the plane sits at a
    /// height no layer occupies yet (a layer is minted the moment you place geometry there).
    /// </summary>
    public StoreyData Storey => Document.StoreyAt(_drawHeight);

    /// <summary>Default wall/ramp/stairs height for freshly drawn primitives (no per-layer height needed).</summary>
    public float DefaultStoreyHeight => Document.DefaultStoreyHeight;

    /// <summary>
    /// How width-based draw tools (ramp, ramp plane, stairs, stair plane) anchor their fixed width to the
    /// two-click line. False (default): the line is the near EDGE — the strip sits on the adjacent tiles
    /// (matches the original grid-aligned behaviour). True: the line is the CENTRELINE — the strip
    /// straddles it, so mirror-image draws come out symmetric. Session-only view state (not undoable, not
    /// saved with the document); toggled from the Primitives palette.
    /// </summary>
    public bool WidthFromCenter { get; set; }
    public PrimitiveRegistry Registry { get; init; }
    public CommandStack Commands { get; init; }
    public LevelView View { get; init; }
    public GridCursor Cursor { get; init; }
    public GridRenderer Grid { get; init; }
    public Node3D PreviewLayer { get; init; }
    public InstancePicker Picker { get; init; }
    public GizmoLayer Gizmos { get; init; }
    /// <summary>Draws the selected path_sweep's control polyline (visual only). Set by Main.</summary>
    public PathOverlay PathOverlay { get; init; }
    /// <summary>Persistent app settings (workspace + target + last level). Set by Main.</summary>
    public AppConfig Config { get; init; }
    /// <summary>Cancels any in-progress tool op before a document swap. Wired by Main to ToolManager.CancelActive.</summary>
    public System.Action CancelActiveTool { get; set; }

    /// <summary>Disk path of the currently open level .tres, or "" if never saved/opened this document.</summary>
    public string CurrentLevelPath { get; private set; } = "";

    private readonly List<string> _selectedIds = new();
    /// <summary>Every selected instance id (Ctrl+click adds/removes). The last entry is the primary.</summary>
    public IReadOnlyList<string> SelectedIds => _selectedIds;
    /// <summary>The primary (last-clicked) selection — drives the inspector + gizmos; null if nothing is
    /// selected. When an opening is selected this is its owning wall.</summary>
    public string SelectedId => _selectedIds.Count > 0 ? _selectedIds[^1] : null;
    /// <summary>Non-null when an opening is selected; <see cref="SelectedId"/> is then its owning wall.</summary>
    public string SelectedOpeningId { get; private set; }

    /// <summary>The active control point of the selected path_sweep (-1 = none). A refinement ON TOP of the
    /// instance selection (the instance stays selected): only this point shows its full move/bank gizmos.
    /// Positional, so it's cleared on any selection change and clamped against the live point count.</summary>
    public int SelectedPathPoint { get; private set; } = -1;

    /// <summary>Which RING the active control point belongs to for a polygon floor: -1 = the outline (and
    /// always -1 for a path_sweep, which has no holes), ≥0 = a hole index. Paired with
    /// <see cref="SelectedPathPoint"/> as the (ring, corner) sub-selection; reset together.</summary>
    public int SelectedHole { get; private set; } = -1;

    /// <summary>True if <paramref name="id"/> is in the current multi-selection.</summary>
    public bool IsSelected(string id) => _selectedIds.Contains(id);

    /// <summary>The live instance data for every selected id (skips any that no longer exist).</summary>
    public List<PrimitiveInstanceData> SelectedInstances()
    {
        var list = new List<PrimitiveInstanceData>();
        foreach (string id in _selectedIds)
        {
            PrimitiveInstanceData inst = GetInstance(id);
            if (inst != null) list.Add(inst);
        }
        return list;
    }

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
    /// User-facing notifications (save/bake/export results, missing-config warnings). The UI layer
    /// subscribes and shows toasts; the console keeps its GD.Print mirror. Same inversion pattern
    /// as <see cref="CancelActiveTool"/> — the context never references UI types.
    /// </summary>
    public event System.Action<NotifyLevel, string> Notified;

    /// <summary>True when the document has edits not yet saved (delegates to the command stack).</summary>
    public bool IsDirty => Commands.IsDirty;

    /// <summary>
    /// Re-syncs everything derived from selection + document state: the view's selection, the live
    /// mesh, and the gizmo handles. The single choke point for "the scene changed" — every command,
    /// selection change, and live drag frame routes through here, so the handle widgets track edits
    /// as they happen. (A live drag holds its handle in SelectTool, so rebuilding this list doesn't
    /// disturb the in-flight drag.)
    /// </summary>
    public void Refresh()
    {
        ClampPathSelection(); // a positional index can go stale after an insert/remove/undo — keep it honest
        View.SetSelection(_selectedIds, SelectedOpeningId);
        _handles = BuildHandles();
        View.Rebuild();
        Gizmos.Rebuild(_handles);
        RenderPathOverlay();
        Changed?.Invoke();
    }

    private List<IEditHandle> BuildHandles()
    {
        if (SelectedOpeningId != null)
        {
            (PrimitiveInstanceData wall, OpeningData opening) = FindOpening(SelectedId, SelectedOpeningId);
            return OpeningHandleProvider.Build(wall, opening, OffsetOfInstance(SelectedId));
        }
        // Resize gizmos only make sense for a single instance — a multi-selection moves as a group (body drag).
        if (_selectedIds.Count != 1) return new List<IEditHandle>();
        PrimitiveInstanceData inst = GetInstance(SelectedId);
        if (inst == null) return new List<IEditHandle>();
        return InstanceHandleProvider.Build(inst, Registry.Get(inst.PrimitiveType), OffsetOfInstance(SelectedId),
            Document.Grid, SelectedPathPoint, SelectedHole, ResetSubSelection);
    }

    /// <summary>Drops the (hole, corner) sub-selection. Passed to the delete-whole-hole handle, which shifts
    /// hole indices, so the selection can't be left pointing at a now-different hole.</summary>
    private void ResetSubSelection() { SelectedHole = -1; SelectedPathPoint = -1; }

    /// <summary>Drops a path-point selection that no longer addresses a live point (count shrank, instance
    /// changed, or it's not a path) so <see cref="DeleteSelected"/> and the gizmo build never read a stale index.</summary>
    /// <summary>True for primitives whose outline is an editable control-point array — path sweep and
    /// polygon floor. They share the Path3D-style point-edit scaffolding (selection, overlay, click-on-line
    /// insert, point delete); a polygon floor is just a closed, planar, bank-free instance of it.</summary>
    private static bool HasEditablePoints(PrimitiveInstanceData inst)
        => inst != null && (inst.PrimitiveType == "path_sweep" || inst.PrimitiveType == "polygon_floor");

    /// <summary>A point-outline primitive's outline is a closed ring (polygon floor always; path sweep when
    /// its "closed" param is set and it has ≥3 points).</summary>
    private static bool OutlineClosed(PrimitiveInstanceData inst, int pointCount)
        => inst.PrimitiveType == "polygon_floor"
            ? pointCount >= 3
            : inst.Parameters.ContainsKey("closed") && inst.Parameters["closed"].AsBool() && pointCount >= 3;

    private void ClampPathSelection()
    {
        if (SelectedPathPoint < 0) { SelectedHole = -1; return; }
        PrimitiveInstanceData inst = _selectedIds.Count == 1 && SelectedOpeningId == null ? GetInstance(SelectedId) : null;

        if (SelectedHole < 0) // outline / path_sweep — unchanged from before holes existed
        {
            int count = HasEditablePoints(inst) ? PathPoints.Read(inst).Count : 0;
            if (SelectedPathPoint >= count) SelectedPathPoint = -1;
        }
        else // a polygon-floor hole corner — the index can go stale after a hole add/remove or undo
        {
            List<List<Vector3>> holes = inst != null && inst.PrimitiveType == "polygon_floor"
                ? PolygonHoles.Decode(inst) : new List<List<Vector3>>();
            if (SelectedHole >= holes.Count || SelectedPathPoint >= holes[SelectedHole].Count)
            { SelectedHole = -1; SelectedPathPoint = -1; }
        }
    }

    /// <summary>Draws the control polyline when (and only when) a single point-outline primitive is selected.</summary>
    private void RenderPathOverlay()
    {
        if (PathOverlay == null) return;
        PrimitiveInstanceData inst = _selectedIds.Count == 1 && SelectedOpeningId == null ? GetInstance(SelectedId) : null;
        if (!HasEditablePoints(inst)) { PathOverlay.Clear(); return; }

        Vector3 off = inst.LocalTransform.Origin + OffsetOfInstance(SelectedId);
        if (inst.PrimitiveType == "polygon_floor")
        {
            // Outline + every hole as separate closed line strips.
            var rings = new List<(IReadOnlyList<Vector3> pts, bool closed)>();
            Godot.Collections.Array<Vector3> outer = PathPoints.Read(inst);
            rings.Add((outer, outer.Count >= 3));
            foreach (List<Vector3> hole in PolygonHoles.Decode(inst)) rings.Add((hole, hole.Count >= 3));
            PathOverlay.ShowMany(rings, off);
        }
        else
        {
            Godot.Collections.Array<Vector3> pts = PathPoints.Read(inst);
            PathOverlay.Show(pts, off, OutlineClosed(inst, pts.Count));
        }
    }

    /// <summary>World offset of the draw plane, where new geometry is drawn.</summary>
    public Vector3 ElevationOffset => new(0, _drawHeight, 0);

    /// <summary>
    /// Moves the draw plane to an absolute elevation: repositions the grid + snap cursor and notifies UI.
    /// View state only — NOT undoable. Always syncs the grid/cursor (so it can be called to initialize),
    /// but only cancels an in-progress draw + fires <see cref="Changed"/> when the height actually moves.
    /// </summary>
    public void SetDrawHeight(float elevation)
    {
        bool moved = !Mathf.IsEqualApprox(elevation, _drawHeight);
        if (moved) CancelActiveTool?.Invoke(); // a half-placed primitive can't straddle heights
        _drawHeight = elevation;
        Cursor.Elevation = elevation;
        if (Grid != null) Grid.Position = new Vector3(0, elevation, 0);
        if (moved) Changed?.Invoke();
    }

    /// <summary>Nudges the draw plane by <paramref name="steps"/> × the document's height step.</summary>
    public void NudgeDrawHeight(int steps) => SetDrawHeight(_drawHeight + steps * Document.Grid.HeightStep);

    /// <summary>World offset of the storey that OWNS <paramref name="id"/> — not necessarily the active one.</summary>
    public Vector3 OffsetOfInstance(string id)
    {
        (StoreyData s, _, _) = Find(id);
        return new Vector3(0, s?.BaseElevation ?? _drawHeight, 0);
    }

    /// <summary>Offset of the storey owning the current selection, so handles/drags sit on the right level.</summary>
    public Vector3 SelectedInstanceOffset => OffsetOfInstance(SelectedId);

    // ---- elevation layers ------------------------------------------------

    /// <summary>Jumps to the next populated layer above; at the top, steps up one default-height "floor".</summary>
    public void StoreyUp() => SwitchStorey(+1);
    /// <summary>Jumps to the next populated layer below; at the bottom, steps down one default-height "floor".</summary>
    public void StoreyDown() => SwitchStorey(-1);

    /// <summary>
    /// Moves the draw plane to a layer's elevation (e.g. clicking a layer row in the scene tree, or at
    /// startup). Clears any selection first, like the old storey switch.
    /// </summary>
    public void SetActiveStorey(StoreyData s)
    {
        ClearSelection();
        SetDrawHeight(s.BaseElevation);
        GD.Print($"[layer] {s.Name}  (base {s.BaseElevation:0.##} m)");
    }

    /// <summary>
    /// Layer-to-layer navigation: jump to the nearest populated layer in <paramref name="dir"/> from the
    /// current height; if none lies that way (we're at the frontier), step the draw plane a full default
    /// height to a fresh, empty elevation where new geometry can be built.
    /// </summary>
    private void SwitchStorey(int dir)
    {
        ClearSelection();
        List<StoreyData> sorted = SortedStoreys(); // ascending by base elevation
        StoreyData next = null;
        if (dir > 0)
        {
            foreach (StoreyData s in sorted)
                if (s.BaseElevation > _drawHeight + 0.001f) { next = s; break; }
        }
        else
        {
            for (int i = sorted.Count - 1; i >= 0; i--)
                if (sorted[i].BaseElevation < _drawHeight - 0.001f) { next = sorted[i]; break; }
        }
        SetDrawHeight(next?.BaseElevation ?? _drawHeight + dir * Document.DefaultStoreyHeight);
    }

    private List<StoreyData> SortedStoreys()
    {
        var list = new List<StoreyData>();
        foreach (StoreyData s in Document.Storeys) list.Add(s);
        list.Sort((a, b) => a.BaseElevation.CompareTo(b.BaseElevation));
        return list;
    }

    public BuildContext BuildCtx() => new()
    {
        Materials = Document.Materials,
        CellSize = Document.Grid.CellSize,
        StoreyHeight = Document.DefaultStoreyHeight,
    };

    public void AddInstance(PrimitiveInstanceData instance)
    {
        DefaultMaterials.ApplyDefaults(instance); // give freshly drawn geometry placeholder textures (no picker UI yet)
        // The instance is built at Origin.Y = 0; it lands in the layer at the current draw height (created
        // lazily by the command if none exists there yet), whose base IS that height — so no Y baking needed.
        Commands.Execute(new AddInstanceCommand(Document, _drawHeight, Document.DefaultStoreyHeight, instance, Refresh));
    }

    /// <summary>Appends a hole ring (≥3 points) to a polygon floor (undoable). No-op if the instance is gone,
    /// isn't a polygon floor, or the ring is too small. Called by the cut-hole tool; supports many holes.</summary>
    public void AddPolygonHole(string instanceId, Godot.Collections.Array<Vector3> ring)
    {
        PrimitiveInstanceData inst = GetInstance(instanceId);
        if (inst == null || inst.PrimitiveType != "polygon_floor" || ring.Count < 3) return;

        List<List<Vector3>> holes = PolygonHoles.Decode(inst);
        (Godot.Collections.Array<Vector3> fromV, Godot.Collections.Array<float> fromS) = PolygonHoles.Encode(holes);

        var added = new List<Vector3>();
        foreach (Vector3 p in ring) added.Add(p);
        holes.Add(added);
        (Godot.Collections.Array<Vector3> toV, Godot.Collections.Array<float> toS) = PolygonHoles.Encode(holes);

        ResetSubSelection(); // a structural change to the hole set — don't leave a stale (hole, corner) active
        Commands.Execute(new SetHolesCommand(inst, fromV, fromS, toV, toS, Refresh));
        GD.Print($"[cut hole] polygon now has {holes.Count} hole(s) in data (skipped ones won't render)");
    }

    /// <summary>
    /// Paints every material slot of an instance with <paramref name="texturePath"/> (undoable).
    /// Ensures the texture exists in the level's material library first. No-op if the instance is gone.
    /// </summary>
    public void AssignTextureToInstance(string instanceId, string texturePath)
    {
        PrimitiveInstanceData inst = GetInstance(instanceId);
        if (inst == null) return;
        IPrimitive prim = Registry.Get(inst.PrimitiveType);
        if (prim == null) return;

        // Registering the texture in the library is deliberately OUTSIDE undo: the MaterialLibrary
        // is an append-only, id-deduped pool of "imported" materials (like DefaultMaterials.Seed),
        // not per-edit state. Only the slot assignment below is undoable. (Undo leaves the entry;
        // harmless — it's reused on redo or any later assignment of the same texture.)
        string materialId = TextureCatalog.EnsureEntry(Document.Materials, texturePath);
        var to = new Dictionary<string, string>();
        foreach (string slot in prim.MaterialSlots) to[slot] = materialId;

        Commands.Execute(new AssignMaterialCommand(inst, to, Refresh));
    }

    /// <summary>
    /// Paints every material slot of every selected instance with <paramref name="texturePath"/> in one
    /// undoable step. Each instance gets its own slot map (primitive types have different slot names).
    /// No-op when nothing is selected or an opening is the selection (openings aren't textured yet).
    /// </summary>
    public void AssignTextureToSelection(string texturePath)
    {
        if (_selectedIds.Count == 0 || SelectedOpeningId != null) return;

        // Register the texture once (append-only library, deliberately outside undo — see AssignTextureToInstance).
        string materialId = TextureCatalog.EnsureEntry(Document.Materials, texturePath);

        var children = new List<ICommand>();
        foreach (string id in _selectedIds)
        {
            PrimitiveInstanceData inst = GetInstance(id);
            IPrimitive prim = inst != null ? Registry.Get(inst.PrimitiveType) : null;
            if (prim == null) continue;
            var to = new Dictionary<string, string>();
            foreach (string slot in prim.MaterialSlots) to[slot] = materialId;
            children.Add(new AssignMaterialCommand(inst, to, () => { }));
        }
        if (children.Count == 0) return;
        Commands.Execute(new MacroCommand($"Texture {children.Count} object(s)", children, Refresh));
    }

    /// <summary>
    /// Texture drop from the viewport onto a specific object: if that object is part of a multi-selection,
    /// paint the whole selection; otherwise just the dropped-on object.
    /// </summary>
    public void AssignTextureFromDrop(string instanceId, string texturePath)
    {
        if (_selectedIds.Count > 1 && IsSelected(instanceId)) AssignTextureToSelection(texturePath);
        else AssignTextureToInstance(instanceId, texturePath);
    }

    /// <summary>
    /// Edits a texture's shared render properties (tiling, tint, pixelation). Affects every instance using
    /// this texture (it's one library entry). Undoable; the resolver cache is busted so the view updates.
    /// No-op if the entry is gone or nothing changed.
    /// </summary>
    public void EditMaterial(string materialId, float uvScale, Color tint, bool pixelated, int pixelSize)
    {
        MaterialEntry entry = Document.Materials.Find(materialId);
        if (entry == null) return;

        var from = new MaterialProps(entry.UvScale, entry.Tint, entry.Pixelated, entry.PixelSize);
        var to = new MaterialProps(uvScale, tint, pixelated, pixelSize);
        if (from == to) return;

        Commands.Execute(new EditMaterialCommand(entry, from, to, () =>
        {
            View.InvalidateMaterial(materialId); // long-lived resolver caches by id — evict before rebuild
            Refresh();
        }));
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
        _selectedIds.Clear();
        if (id != null) _selectedIds.Add(id);
        SelectedOpeningId = null;
        SelectedPathPoint = -1;
        SelectedHole = -1;
        Refresh();
    }

    /// <summary>Makes <paramref name="index"/> the active control point of the outline / a path (-1 to clear).</summary>
    public void SelectPathPoint(int index) => SelectRingPoint(-1, index);

    /// <summary>Makes corner <paramref name="index"/> of ring <paramref name="ring"/> active (ring -1 = the
    /// outline / a path's points; ≥0 = a polygon-floor hole). Transient view state, NOT a command — the
    /// instance selection is untouched; only which corner shows its gizmos changes.</summary>
    public void SelectRingPoint(int ring, int index)
    {
        SelectedHole = ring;
        SelectedPathPoint = index;
        Refresh();
    }

    /// <summary>
    /// Click-on-line insert (Path3D-style): if a single path_sweep is selected and the cursor ray lands
    /// near one of its segments, inserts a new control point there (grid-snapped, bank interpolated) and
    /// makes it the active point. Returns false (so the caller can fall through to a normal click) when no
    /// path is selected or the click isn't close enough to the line. Undoable.
    /// </summary>
    public bool TryInsertPathPointAtCursor()
    {
        if (_selectedIds.Count != 1 || SelectedOpeningId != null) return false;
        PrimitiveInstanceData inst = GetInstance(SelectedId);
        if (!HasEditablePoints(inst)) return false;

        Godot.Collections.Array<Vector3> pts = PathPoints.Read(inst);
        if (pts.Count < 2) return false;
        if (!Picker.MouseRay(out Vector3 from, out Vector3 dir)) return false;

        Vector3 off = inst.LocalTransform.Origin + OffsetOfInstance(SelectedId);
        bool closed = OutlineClosed(inst, pts.Count);
        int segCount = closed ? pts.Count : pts.Count - 1;

        // Nearest segment to the cursor RAY in 3D (so a climbing/looping path works, not just a flat one).
        float bestDist = float.MaxValue;
        int bestSeg = -1;
        float bestT = 0f;
        Vector3 bestWorld = default;
        for (int i = 0; i < segCount; i++)
        {
            GizmoMath.ClosestRaySegment(from, dir, off + pts[i], off + pts[(i + 1) % pts.Count],
                out Vector3 segPoint, out float t, out float d);
            if (d < bestDist) { bestDist = d; bestSeg = i; bestT = t; bestWorld = segPoint; }
        }

        // Accept only if the click landed within a few pixels of the line on screen (zoom-independent —
        // a world threshold goes sub-pixel when zoomed out). Off the line → false, so the click clears.
        const float PixelThreshold = 10f;
        if (bestSeg < 0 || !Picker.WorldToScreen(bestWorld, out Vector2 screen)
            || screen.DistanceTo(Picker.MouseScreen()) > PixelThreshold) return false;

        Vector3 local = bestWorld - off;
        float cell = Document.Grid.CellSize;
        var newPt = new Vector3(Mathf.Round(local.X / cell) * cell, local.Y, Mathf.Round(local.Z / cell) * cell);
        int at = bestSeg + 1;

        Godot.Collections.Array<Vector3> toPts = pts.Duplicate();
        toPts.Insert(at, newPt);
        SelectedHole = -1;      // insert targets the OUTER ring (hole-line insert is a later slice)
        SelectedPathPoint = at; // the command's Refresh then unfolds the new point's gizmos

        if (inst.PrimitiveType == "path_sweep")
        {
            // Path: keep the parallel bank array length-aligned (new point gets the segment's lerped bank).
            Godot.Collections.Array<float> banks = PathPoints.ReadBanks(inst, pts.Count);
            Godot.Collections.Array<float> toBanks = banks.Duplicate();
            toBanks.Insert(at, Mathf.Lerp(banks[bestSeg], banks[(bestSeg + 1) % pts.Count], bestT));
            Commands.Execute(new EditPathCommand(inst, pts, banks, toPts, toBanks, Refresh));
        }
        else
        {
            Commands.Execute(new EditPointsCommand(inst, pts, toPts, Refresh)); // polygon floor: points only
        }
        return true;
    }

    /// <summary>Replaces the selection with exactly <paramref name="ids"/> (order preserved, last = primary).
    /// Used by the scene tree, where the whole selected set is read at once. Drops any opening selection.</summary>
    public void SelectMany(IReadOnlyList<string> ids)
    {
        _selectedIds.Clear();
        foreach (string id in ids)
            if (id != null && !_selectedIds.Contains(id)) _selectedIds.Add(id);
        SelectedOpeningId = null;
        SelectedPathPoint = -1;
        SelectedHole = -1;
        Refresh();
    }

    /// <summary>Ctrl+click: add <paramref name="id"/> to the selection (as the new primary) if absent, else
    /// remove it. Always drops any opening selection — multi-select is instances-only.</summary>
    public void ToggleSelect(string id)
    {
        if (id == null) return;
        SelectedOpeningId = null;
        SelectedPathPoint = -1;
        SelectedHole = -1;
        if (!_selectedIds.Remove(id)) _selectedIds.Add(id);
        Refresh();
    }

    /// <summary>Selects an opening: the wall is drawn intact and the opening shows as a solid placeholder.</summary>
    public void SelectOpening(string wallId, string openingId)
    {
        _selectedIds.Clear();
        _selectedIds.Add(wallId);
        SelectedOpeningId = openingId;
        SelectedPathPoint = -1;
        Refresh();
    }

    public void ClearSelection()
    {
        if (_selectedIds.Count == 0 && SelectedOpeningId == null && SelectedPathPoint < 0) return;
        _selectedIds.Clear();
        SelectedOpeningId = null;
        SelectedPathPoint = -1;
        SelectedHole = -1;
        Refresh();
    }

    public void DeleteSelected()
    {
        // A selected HOLE corner: Delete removes that corner, or the whole hole if it would drop below a
        // triangle. (Checked before the outline branch, which also matches SelectedPathPoint >= 0.)
        if (SelectedHole >= 0 && SelectedPathPoint >= 0 && SelectedOpeningId == null && _selectedIds.Count == 1)
        {
            PrimitiveInstanceData poly = GetInstance(SelectedId);
            if (poly != null && poly.PrimitiveType == "polygon_floor")
            {
                List<List<Vector3>> from = PolygonHoles.Decode(poly);
                if (SelectedHole < from.Count && SelectedPathPoint < from[SelectedHole].Count)
                {
                    List<List<Vector3>> to = PolygonHoleOps.Clone(from);
                    to[SelectedHole].RemoveAt(SelectedPathPoint);
                    if (to[SelectedHole].Count < 3) to.RemoveAt(SelectedHole); // hole fell below a triangle
                    SelectedHole = -1; SelectedPathPoint = -1;
                    Commands.Execute(PolygonHoleOps.Command(poly, from, to, Refresh));
                }
                return; // a hole corner was the target — don't fall through to the outline / instance delete
            }
        }

        // A selected outline control point: Delete removes just that point (not the whole instance), as long
        // as enough remain to stay drawable — a path keeps ≥2 (a line), a polygon floor ≥3 (a triangle).
        if (SelectedPathPoint >= 0 && SelectedOpeningId == null && _selectedIds.Count == 1)
        {
            PrimitiveInstanceData outline = GetInstance(SelectedId);
            if (HasEditablePoints(outline))
            {
                Godot.Collections.Array<Vector3> pts = PathPoints.Read(outline);
                int min = outline.PrimitiveType == "polygon_floor" ? 3 : 2;
                if (SelectedPathPoint < pts.Count && pts.Count > min)
                {
                    int removed = SelectedPathPoint;
                    Godot.Collections.Array<Vector3> toPts = pts.Duplicate();
                    toPts.RemoveAt(removed);
                    SelectedPathPoint = -1;
                    if (outline.PrimitiveType == "path_sweep")
                    {
                        Godot.Collections.Array<float> banks = PathPoints.ReadBanks(outline, pts.Count);
                        Godot.Collections.Array<float> toBanks = banks.Duplicate();
                        toBanks.RemoveAt(removed);
                        Commands.Execute(new EditPathCommand(outline, pts, banks, toPts, toBanks, Refresh));
                    }
                    else
                    {
                        Commands.Execute(new EditPointsCommand(outline, pts, toPts, Refresh));
                    }
                }
                return; // an outline point was the selection target — don't fall through to instance delete
            }
        }

        if (SelectedOpeningId != null)
        {
            (PrimitiveInstanceData wall, OpeningData opening) = FindOpening(SelectedId, SelectedOpeningId);
            if (opening == null) { ClearSelection(); return; }

            _selectedIds.Clear();
            SelectedOpeningId = null; // command's refresh will rebuild without the placeholder
            Commands.Execute(new RemoveOpeningCommand(wall, opening, Refresh));
            return;
        }

        if (_selectedIds.Count == 0) return;

        // Snapshot the (storey, instance, index) of every selected id before dropping the selection.
        var found = new List<(StoreyData storey, PrimitiveInstanceData inst, int index)>();
        foreach (string id in _selectedIds)
        {
            (StoreyData s, PrimitiveInstanceData inst, int index) = Find(id);
            if (inst != null) found.Add((s, inst, index));
        }
        _selectedIds.Clear(); // command's refresh will rebuild without the highlight
        if (found.Count == 0) { Refresh(); return; }

        // Descending index so the macro's reverse-order undo re-inserts ascending (original order preserved).
        // Removal itself is by-reference (index-safe), so the descending order only matters for undo.
        found.Sort((a, b) => b.index.CompareTo(a.index));
        var children = new List<ICommand>(found.Count);
        foreach ((StoreyData s, PrimitiveInstanceData inst, int index) in found)
            children.Add(new RemoveInstanceCommand(s, inst, index, () => { }));
        Commands.Execute(new MacroCommand($"Delete {children.Count} object(s)", children, Refresh));
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

    private const string BakedDir = "res://Baked";

    // ---- document lifecycle (New / Open / Save) --------------------------

    /// <summary>
    /// Swaps in a different document and re-points everything derived from it: cancels any in-progress
    /// tool op, re-targets the live view, clears the undo history + selection, and activates the
    /// document's first storey. Panels resync via the <see cref="Changed"/> event fired from Refresh.
    /// </summary>
    public void ReplaceDocument(LevelDocument doc)
    {
        CancelActiveTool?.Invoke(); // a half-drawn primitive must not straddle two documents

        if (doc.Storeys.Count == 0) // a malformed/empty load still needs a storey to edit on
            doc.Storeys.Add(new StoreyData { Id = Ids.New(), Name = "Ground Floor", BaseElevation = 0f, Height = 3f });

        Document = doc;
        _selectedIds.Clear();
        SelectedOpeningId = null;
        SelectedPathPoint = -1;
        SelectedHole = -1;
        View.Setup(doc, Registry); // re-target + drop stale material cache; Rebuild (below) clears old meshes
        Commands.Clear();
        SetActiveStorey(doc.Storeys[0]); // positions grid/cursor at the ground storey
        Refresh();                       // rebuild the view for the new document
    }

    /// <summary>Starts a fresh, empty level (discards unsaved edits — explicit-save workflow).</summary>
    public void NewLevel()
    {
        ReplaceDocument(LevelDocument.CreateEmpty());
        CurrentLevelPath = "";
    }

    /// <summary>Opens an editable level .tres from disk and makes it the active document. Returns success.</summary>
    public bool OpenLevel(string path)
    {
        LevelDocument doc = LevelSerializer.Load(path);
        if (doc == null)
        {
            GD.PrintErr($"[open] could not load {path}");
            Notified?.Invoke(NotifyLevel.Error, $"Could not open {path.GetFile()}");
            return false;
        }
        ReplaceDocument(doc);
        CurrentLevelPath = path;
        RememberLastLevel(path);
        Notified?.Invoke(NotifyLevel.Info, $"Opened {doc.Name}");
        return true;
    }

    /// <summary>Saves the editable source .tres into the workspace's levels/ folder (re-openable).</summary>
    public void SaveSource()
    {
        if (!Workspace.IsSet)
        {
            GD.PushWarning("[save] no workspace set — pick a workspace folder first (Project tab).");
            Notified?.Invoke(NotifyLevel.Warning, "No workspace set — pick a folder in the Project tab first.");
            return;
        }
        EnsureDir(Workspace.LevelsDir);
        string path = $"{Workspace.LevelsDir}/{FileStem()}.tres";
        Error e = LevelSerializer.Save(Document, path);
        Report("save", path, e);
        if (e == Error.Ok)
        {
            CurrentLevelPath = path;
            RememberLastLevel(path);
            Commands.MarkSaved(); // this state is now the clean baseline for dirty tracking
        }
    }

    private void RememberLastLevel(string path)
    {
        if (Config == null) return;
        Config.LastLevelPath = path;
        Config.Save();
    }

    /// <summary>Bakes a game-ready .tscn (meshes + collision) you can open in Godot.</summary>
    public void BakeToGodot()
    {
        EnsureDir(BakedDir);
        string path = $"{BakedDir}/{FileStem()}.tscn";
        Error e = new SceneBaker(Registry).BakeToFile(Document, path);
        Report("bake", path, e);
    }

    /// <summary>Bakes a single merged "chunk" .tscn: geometry merged by material (one MeshInstance3D
    /// per material) + one precise trimesh collision. Fewest draw calls; for assembling maps from
    /// chunks. Separate output (<c>_merged.tscn</c>) — does not overwrite the per-instance bake.</summary>
    public void BakeMergedToGodot()
    {
        EnsureDir(BakedDir);
        string path = $"{BakedDir}/{FileStem()}_merged.tscn";
        Error e = new SceneBaker(Registry).BakeMergedToFile(Document, path);
        Report("bake merged", path, e);
    }

    /// <summary>
    /// Exports straight into the target game project (<c>&lt;target&gt;/levels/&lt;Name&gt;.tscn</c>) with
    /// textures <b>embedded inline</b>, so the .tscn is self-contained and drops into that project with
    /// no res:// dependency on the builder. Uses an absolute OS path outside this project.
    ///
    /// <paramref name="merged"/> = true (default) writes a single merged chunk (one mesh per material +
    /// one trimesh collision; fewest draw calls). false writes the per-object tree (one MeshInstance3D
    /// per primitive + per-object collision shapes) so individual pieces stay selectable/movable in the
    /// Godot editor.
    /// </summary>
    public void ExportToGame(bool merged = true)
    {
        if (Config == null || !Config.HasTarget)
        {
            GD.PushWarning("[export] no target game project set — pick one in the Project tab first.");
            Notified?.Invoke(NotifyLevel.Warning, "No target game project set — pick one in the Project tab first.");
            return;
        }
        string dir = $"{Config.TargetProjectPath}/levels";
        EnsureDir(dir);
        string path = $"{dir}/{FileStem()}.tscn";
        var baker = new SceneBaker(Registry);
        Error e = merged
            ? baker.BakeMergedToFile(Document, path, embedTextures: true)
            : baker.BakeToFile(Document, path, embedTextures: true);
        Report("export", path, e);
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

    private void Report(string action, string path, Error e)
    {
        if (e == Error.Ok)
        {
            GD.Print($"[{action}] wrote {path}");
            Notified?.Invoke(NotifyLevel.Success, $"{Capitalize(action)} OK: {path.GetFile()}");
        }
        else
        {
            GD.PrintErr($"[{action}] failed ({e}) for {path}");
            Notified?.Invoke(NotifyLevel.Error, $"{Capitalize(action)} failed ({e})");
        }
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : $"{char.ToUpperInvariant(s[0])}{s[1..]}";
}
