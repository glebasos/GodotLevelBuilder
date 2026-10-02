using System.Collections.Generic;
using System.Text;
using Godot;
using LevelBuilder.Core.Data;
using LevelBuilder.Editor.Session;

namespace LevelBuilder.UI;

/// <summary>
/// Scene-tree dock — the document hierarchy as a clickable tree:
///
///   Storey ▸ Primitive ▸ Opening
///
/// Two-way bound to <see cref="EditorContext"/>: clicking a row selects that instance/opening
/// (or activates that storey), and the editor's own selection drives the highlight back here.
///
/// It listens to <see cref="EditorContext.Changed"/>, which fires on *every* edit — including
/// each live-drag frame — so it gates work on a cheap structural signature: a full rebuild only
/// when storeys/instances/openings actually change, otherwise just move the highlight. That keeps
/// drags free and preserves the user's expand/collapse state. FocusMode is None on purpose: a
/// focused Tree captures letter keys for type-ahead search, which would swallow the F/W/S tool
/// hotkeys the instant someone clicked a row.
/// </summary>
public partial class SceneTreePanel : PanelContainer
{
    private EditorContext _ctx;
    private Tree _tree;
    private string _structureSig = "";
    private bool _suppressSignal;
    private bool _rebuildQueued;
    private bool _syncQueued;
    private readonly Dictionary<string, TreeItem> _itemsByKey = new();

    private static readonly Color ActiveStoreyColor = new(0.55f, 0.80f, 1.0f);

    /// <summary>Wires the panel to the editor and builds the UI. Call after adding to the tree.</summary>
    public void Setup(EditorContext ctx)
    {
        _ctx = ctx;
        CustomMinimumSize = new Vector2(UiConstants.SceneTreeWidth, 0);

        var vbox = new VBoxContainer();
        AddChild(vbox);

        vbox.AddChild(new Label { Text = "  Scene" });

        _tree = new Tree
        {
            HideRoot = true,
            // Multi lets the Tree handle Ctrl-toggle + Shift-range natively and render several rows
            // highlighted at once; we translate its selection into the editor model (instances only).
            SelectMode = Tree.SelectModeEnum.Multi,
            FocusMode = FocusModeEnum.None, // keep the tree's type-ahead from eating tool hotkeys
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tree.MultiSelected += OnMultiSelected;
        _tree.NothingSelected += OnNothingSelected;
        vbox.AddChild(_tree);

        _ctx.Changed += OnDocumentChanged;
        Rebuild();
    }

    public override void _ExitTree()
    {
        if (_ctx != null) _ctx.Changed -= OnDocumentChanged;
    }

    private void OnDocumentChanged()
    {
        if (StructureSignature() != _structureSig)
        {
            // Rebuild mutates the Tree (Clear/CreateItem). When the change originated from this Tree's own
            // item_selected signal (e.g. clicking a storey row activates it), the Tree is "blocked" mid-emit
            // and those calls fail ("blocked > 0"). Defer so the rebuild runs after the signal returns.
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Callable.From(() => { _rebuildQueued = false; Rebuild(); }).CallDeferred();
        }
        else SyncSelection(); // structure unchanged (e.g. a live drag): just keep the highlight in step
    }

    // ---- build -----------------------------------------------------------

    private void Rebuild()
    {
        _structureSig = StructureSignature();
        _itemsByKey.Clear();
        _tree.Clear();
        TreeItem root = _tree.CreateItem(); // hidden root

        foreach (StoreyData storey in SortedStoreys())
        {
            if (!ShouldShow(storey)) continue;

            bool active = ReferenceEquals(storey, _ctx.Storey);
            TreeItem sItem = AddRow(root, $"s|{storey.Id}", StoreyLabel(storey, active));
            if (active) sItem.SetCustomColor(0, ActiveStoreyColor);

            foreach (PrimitiveInstanceData inst in storey.Instances)
            {
                TreeItem iItem = AddRow(sItem, $"i|{inst.Id}", InstanceLabel(inst));
                foreach (OpeningData opening in inst.Openings)
                    AddRow(iItem, $"o|{inst.Id}|{opening.Id}", OpeningLabel(opening));
            }
        }

        SyncSelection();
    }

    private TreeItem AddRow(TreeItem parent, string key, string text)
    {
        TreeItem item = _tree.CreateItem(parent);
        item.SetText(0, text);
        item.SetMetadata(0, key);
        _itemsByKey[key] = item;
        return item;
    }

    /// <summary>
    /// Mirror the editor's current selection into the tree (model → highlight). In Multi mode several
    /// rows can show selected at once: every selected instance is highlighted; an opening selection
    /// highlights its single row. Wrapped in <see cref="_suppressSignal"/> so our own Select/Deselect
    /// calls don't echo back through <see cref="OnMultiSelected"/>.
    /// </summary>
    private void SyncSelection()
    {
        // The model's desired selected-row keys (in primary-last order).
        var desired = new List<string>();
        if (_ctx.SelectedOpeningId != null)
            desired.Add($"o|{_ctx.SelectedId}|{_ctx.SelectedOpeningId}");
        else
            foreach (string id in _ctx.SelectedIds) desired.Add($"i|{id}");

        // Skip when the tree already matches the model. This is load-bearing, not just an optimization:
        // an unconditional DeselectAll/Select moves the Tree's native Shift-range *anchor* to the last
        // re-selected row, which would break extending a range. It also avoids per-drag-frame churn and
        // bounds any selection-signal feedback (after a real change, tree==model, so the next sync skips).
        if (SelectionMatches(desired)) return;

        _suppressSignal = true;
        _tree.DeselectAll();
        TreeItem scrollTo = null;
        foreach (string key in desired)
            if (_itemsByKey.TryGetValue(key, out TreeItem item))
            {
                item.Select(0);
                scrollTo = item; // last one wins → scroll to the primary
            }
        if (scrollTo != null) _tree.ScrollToItem(scrollTo);
        _suppressSignal = false;
    }

    /// <summary>True if the Tree's currently-selected rows are exactly <paramref name="desired"/> (as a set).</summary>
    private bool SelectionMatches(List<string> desired)
    {
        int count = 0;
        for (TreeItem it = _tree.GetNextSelected(null); it != null; it = _tree.GetNextSelected(it))
        {
            if (!desired.Contains(it.GetMetadata(0).AsString())) return false;
            count++;
        }
        return count == desired.Count;
    }

    // ---- input -----------------------------------------------------------

    // The Tree fires MultiSelected once per affected row (a Shift-range fires several). Rather than
    // react to each, defer one read of the *whole* selection so the model is set from the final state.
    private void OnMultiSelected(TreeItem item, long column, bool selected) => QueueTreeSync();
    private void OnNothingSelected() => QueueTreeSync();

    private void QueueTreeSync()
    {
        if (_suppressSignal || _syncQueued) return;
        _syncQueued = true;
        Callable.From(() => { _syncQueued = false; SyncFromTree(); }).CallDeferred();
    }

    /// <summary>
    /// Translate the Tree's native multi-selection into the editor model (tree → model). Instances win:
    /// any selected instance rows become the multi-selection (storey/opening rows in the same range are
    /// ignored). With no instances, a single opening or storey row drives its single-select / activate.
    /// </summary>
    private void SyncFromTree()
    {
        var instanceIds = new List<string>();
        string[] firstOpening = null;
        string firstStorey = null;

        for (TreeItem it = _tree.GetNextSelected(null); it != null; it = _tree.GetNextSelected(it))
        {
            string[] parts = it.GetMetadata(0).AsString().Split('|');
            switch (parts[0])
            {
                case "i": instanceIds.Add(parts[1]); break;
                case "o": firstOpening ??= parts; break;
                case "s": firstStorey ??= parts[1]; break;
            }
        }

        if (instanceIds.Count > 0)
        {
            _ctx.SelectMany(instanceIds); // Refresh → SyncSelection normalizes the tree (drops stray storey/opening rows)
        }
        else if (firstOpening != null)
        {
            _ctx.SelectOpening(firstOpening[1], firstOpening[2]);
        }
        else if (firstStorey != null)
        {
            StoreyData storey = FindStorey(firstStorey);
            if (storey != null) _ctx.SetActiveStorey(storey);
        }
        else
        {
            _ctx.ClearSelection();
        }
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// An empty storey is hidden until it gets its first object — the tree only shows storeys you've
    /// actually built in. The ground floor (the originally-seeded storey, always <c>Storeys[0]</c>) is
    /// the one exception: it stays visible as the default working level even while empty.
    /// </summary>
    private bool ShouldShow(StoreyData s) => s.Instances.Count > 0 || IsGround(s);

    private bool IsGround(StoreyData s)
        => _ctx.Document.Storeys.Count > 0 && ReferenceEquals(s, _ctx.Document.Storeys[0]);

    /// <summary>A cheap fingerprint of the hierarchy + active storey; a change means "rebuild".</summary>
    private string StructureSignature()
    {
        var sb = new StringBuilder();
        sb.Append(_ctx.Storey?.Id).Append(';');
        foreach (StoreyData s in SortedStoreys())
        {
            sb.Append(s.Id).Append(':');
            foreach (PrimitiveInstanceData i in s.Instances)
            {
                sb.Append(i.Id).Append(',');
                foreach (OpeningData o in i.Openings) sb.Append(o.Id).Append('.');
            }
            sb.Append('|');
        }
        return sb.ToString();
    }

    /// <summary>Upper storeys on top, like a building section.</summary>
    private List<StoreyData> SortedStoreys()
    {
        var list = new List<StoreyData>();
        foreach (StoreyData s in _ctx.Document.Storeys) list.Add(s);
        list.Sort((a, b) => b.BaseElevation.CompareTo(a.BaseElevation));
        return list;
    }

    private StoreyData FindStorey(string id)
    {
        foreach (StoreyData s in _ctx.Document.Storeys)
            if (s.Id == id) return s;
        return null;
    }

    private static string StoreyLabel(StoreyData s, bool active)
    {
        string name = string.IsNullOrEmpty(s.Name) ? "Storey" : s.Name;
        return $"{name}  ({s.BaseElevation:0.##} m){(active ? "  ●" : "")}";
    }

    private string InstanceLabel(PrimitiveInstanceData inst)
        => $"{UiFactory.TypeLabel(_ctx.Registry, inst.PrimitiveType)}  ({Short(inst.Id)})";

    private static string OpeningLabel(OpeningData o)
        => $"{UiFactory.OpeningLabel(o)}  ({Short(o.Id)})";

    /// <summary>Last 4 chars of an id, for a compact disambiguator (tree rows stay narrow).</summary>
    private static string Short(string id) => UiFactory.ShortId(id, 4);
}
