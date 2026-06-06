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
    private readonly Dictionary<string, TreeItem> _itemsByKey = new();

    private static readonly Color ActiveStoreyColor = new(0.55f, 0.80f, 1.0f);

    /// <summary>Wires the panel to the editor and builds the UI. Call after adding to the tree.</summary>
    public void Setup(EditorContext ctx)
    {
        _ctx = ctx;
        CustomMinimumSize = new Vector2(240, 0);

        var vbox = new VBoxContainer();
        AddChild(vbox);

        vbox.AddChild(new Label { Text = "  Scene" });

        _tree = new Tree
        {
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Single,
            FocusMode = FocusModeEnum.None, // keep the tree's type-ahead from eating tool hotkeys
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tree.ItemSelected += OnItemSelected;
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
        if (StructureSignature() != _structureSig) Rebuild();
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

    /// <summary>Mirror the editor's current selection into the tree without re-triggering selection.</summary>
    private void SyncSelection()
    {
        string key = _ctx.SelectedOpeningId != null ? $"o|{_ctx.SelectedId}|{_ctx.SelectedOpeningId}"
                   : _ctx.SelectedId != null ? $"i|{_ctx.SelectedId}"
                   : null;

        _suppressSignal = true;
        if (key != null && _itemsByKey.TryGetValue(key, out TreeItem item))
        {
            item.Select(0);
            _tree.ScrollToItem(item);
        }
        else
        {
            _tree.DeselectAll();
        }
        _suppressSignal = false;
    }

    // ---- input -----------------------------------------------------------

    private void OnItemSelected()
    {
        if (_suppressSignal) return;
        TreeItem item = _tree.GetSelected();
        if (item == null) return;

        string[] parts = item.GetMetadata(0).AsString().Split('|');
        switch (parts[0])
        {
            case "s":
                StoreyData storey = FindStorey(parts[1]);
                if (storey != null) _ctx.SetActiveStorey(storey);
                break;
            case "i":
                _ctx.Select(parts[1]);
                break;
            case "o":
                _ctx.SelectOpening(parts[1], parts[2]);
                break;
        }
    }

    // ---- helpers ---------------------------------------------------------

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

    private static string InstanceLabel(PrimitiveInstanceData inst)
    {
        string type = string.IsNullOrEmpty(inst.PrimitiveType) ? "primitive" : inst.PrimitiveType;
        return $"{char.ToUpperInvariant(type[0])}{type[1..]}  ({Short(inst.Id)})";
    }

    private static string OpeningLabel(OpeningData o)
        => $"{(o.SillHeight > 0 ? "Window" : "Door")}  ({Short(o.Id)})";

    /// <summary>Last 4 chars of an id, for a compact disambiguator.</summary>
    private static string Short(string id)
        => string.IsNullOrEmpty(id) ? "?" : id.Length <= 4 ? id : id[^4..];
}
