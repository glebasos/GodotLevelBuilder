using System.Collections.Generic;
using Godot;
using LevelBuilder.Core.Build;
using LevelBuilder.Core.Data;
using LevelBuilder.Core.Geometry;
using LevelBuilder.Core.Primitives;

namespace LevelBuilder.Editor.View;

/// <summary>
/// Live preview of the document: per primitive instance, a visible MeshInstance3D plus a
/// StaticBody3D pick collider tagged with the instance Id (for selection). Regenerated from
/// the same BuildMesh the baker uses. M2 rebuilds everything on any change (tiny levels).
/// Surface materials are resolved from the level's MaterialLibrary (same as the baker) via a
/// shared MaterialResolver; the selected instance gets a highlight override on top.
/// </summary>
public partial class LevelView : Node3D
{
    private LevelDocument _doc;
    private PrimitiveRegistry _registry;
    private readonly HashSet<string> _selectedIds = new();
    private string _selectedOpeningId;
    private readonly MaterialResolver _materials = new();

    public void Setup(LevelDocument doc, PrimitiveRegistry registry)
    {
        _doc = doc;
        _registry = registry;
        _materials.Clear(); // a new document has its own MaterialLibrary — don't serve a stale cached material by id
    }

    /// <summary>
    /// Drops a material's cached build so the next <see cref="Rebuild"/> re-resolves it. The resolver
    /// here is long-lived (cached across rebuilds), so a property edit on its MaterialEntry wouldn't
    /// show until the cache entry is evicted. Called when a texture's properties change.
    /// </summary>
    public void InvalidateMaterial(string id) => _materials.Invalidate(id);

    /// <summary>Stores the selection state; the caller drives the rebuild (see EditorContext.Refresh).</summary>
    public void SetSelection(IReadOnlyCollection<string> instanceIds, string openingId)
    {
        _selectedIds.Clear();
        foreach (string id in instanceIds) _selectedIds.Add(id);
        _selectedOpeningId = openingId;
    }

    public void Rebuild()
    {
        foreach (Node child in GetChildren())
            child.QueueFree();

        // Trigger wiring overlay (Super Corgi Ball): channel → trigger zone tops / triggered piece centres.
        var triggers = new List<(int channel, Vector3 at)>();
        var listeners = new List<(int channel, Vector3 at)>();

        foreach (StoreyData storey in _doc.Storeys)
        {
            var ctx = new BuildContext
            {
                Materials = _doc.Materials,
                CellSize = _doc.Grid.CellSize,
                StoreyHeight = storey.Height,
            };
            var offset = new Vector3(0, storey.BaseElevation, 0);

            foreach (PrimitiveInstanceData inst in storey.Instances)
            {
                IPrimitive prim = _registry.Get(inst.PrimitiveType);
                if (prim == null) continue;

                Transform3D xform = inst.LocalTransform;
                xform.Origin += offset;

                // While an opening is selected we draw the wall *intact* (its hole suppressed) and
                // show the opening as a solid placeholder — purely an edit-time view. The pick body
                // below is still built from the unfiltered instance (holed collision), so the
                // opening's pick box stays the sole occupant of the void. Bake/save never see this.
                bool ownsSelectedOpening = _selectedOpeningId != null && _selectedIds.Contains(inst.Id);
                PrimitiveInstanceData meshSource = ownsSelectedOpening ? WithoutOpening(inst, _selectedOpeningId) : inst;

                ArrayMesh mesh = prim.BuildMesh(meshSource, ctx);
                _materials.AssignSurfaceMaterials(mesh, prim, meshSource, _doc.Materials); // same surfaces the baker writes

                AddChild(new MeshInstance3D
                {
                    Mesh = mesh,
                    Transform = xform,
                    // A selected (non-opening) instance gets the highlight as a translucent OVERLAY (not an
                    // override): the overlay composites on top of the real surface materials, so the object's
                    // texture stays visible — tinted orange — instead of being hidden behind solid orange.
                    // That matters because texture properties (tiling/tint) are edited while selected, so the
                    // texture must show through for the change to be visible live.
                    MaterialOverlay = (_selectedOpeningId == null && _selectedIds.Contains(inst.Id)) ? HighlightMaterial() : null,
                });

                AddChild(BuildPickBody(inst, prim, ctx, xform));
                AddOpeningBodies(inst, xform);
                AddMotionGhost(inst, prim, mesh, xform);
                CollectWiring(inst, prim, mesh, xform, triggers, listeners);
            }
        }
        AddWiring(triggers, listeners);
    }

    private static void CollectWiring(PrimitiveInstanceData inst, IPrimitive prim, ArrayMesh mesh, Transform3D xform,
        List<(int, Vector3)> triggers, List<(int, Vector3)> listeners)
    {
        if (prim is TriggerMarkerPrimitive)
        {
            int ch = inst.Parameters.ContainsKey("channel") ? inst.Parameters["channel"].AsInt32() : 1;
            float h = inst.Parameters.ContainsKey("height") ? inst.Parameters["height"].AsSingle() : 3f;
            triggers.Add((ch, xform.Origin + Vector3.Up * h));
        }
        else if (prim is not MarkerPrimitive && Motion.Mode(inst) == Motion.Triggered)
        {
            int ch = inst.Parameters.ContainsKey("m_channel") ? inst.Parameters["m_channel"].AsInt32() : 1;
            Aabb box = mesh.GetAabb();
            listeners.Add((ch, xform * (box.GetCenter() + new Vector3(0, box.Size.Y * 0.5f, 0))));
        }
    }

    /// <summary>
    /// Makes the invisible channel wiring visible: a "CH n" label over every trigger zone and every piece
    /// moving "On trigger", and a yellow line from each trigger to each piece on its channel. A listener
    /// with no trigger on its channel gets a red label (it would never move). Edit-time only.
    /// </summary>
    private void AddWiring(List<(int channel, Vector3 at)> triggers, List<(int channel, Vector3 at)> listeners)
    {
        var fired = new HashSet<int>();
        foreach ((int ch, Vector3 at) in triggers)
        {
            fired.Add(ch);
            AddChild(ChannelLabel($"CH {ch}", at, new Color(1f, 0.9f, 0.2f)));
        }
        foreach ((int ch, Vector3 at) in listeners)
            AddChild(ChannelLabel(fired.Contains(ch) ? $"CH {ch}" : $"CH {ch} (no trigger)", at,
                                  fired.Contains(ch) ? new Color(1f, 0.9f, 0.2f) : new Color(1f, 0.35f, 0.3f)));

        var lines = new ImmediateMesh();
        bool any = false;
        foreach ((int tch, Vector3 from) in triggers)
            foreach ((int lch, Vector3 to) in listeners)
            {
                if (tch != lch) continue;
                if (!any) { lines.SurfaceBegin(Mesh.PrimitiveType.Lines); any = true; }
                lines.SurfaceAddVertex(from);
                lines.SurfaceAddVertex(to);
            }
        if (!any) return;
        lines.SurfaceEnd();
        AddChild(new MeshInstance3D
        {
            Mesh = lines,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 0.9f, 0.2f),
                NoDepthTest = true,
            },
        });
    }

    private static Label3D ChannelLabel(string text, Vector3 at, Color color) => new()
    {
        Text = text,
        Position = at + Vector3.Up * 0.4f,
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        NoDepthTest = true,
        FontSize = 48,
        PixelSize = 0.008f,
        Modulate = color,
        OutlineSize = 10,
    };

    /// <summary>
    /// For each opening on a wall, a box pick collider (tagged wall + opening id) so the hole is
    /// clickable; the selected opening also gets a solid coloured placeholder mesh.
    /// </summary>
    private void AddOpeningBodies(PrimitiveInstanceData inst, Transform3D wallXform)
    {
        if (inst.PrimitiveType != "wall" || inst.Openings.Count == 0) return;
        float length = GetF(inst, "length", 1f);
        float thickness = GetF(inst, "thickness", 0.2f);

        foreach (OpeningData o in inst.Openings)
        {
            (Vector3 size, Transform3D localCenter) = OpeningGeometry.LocalBox(o, length, thickness);
            Transform3D world = wallXform * localCenter;

            var body = new StaticBody3D { Transform = world, CollisionLayer = Session.InstancePicker.OpeningLayer };
            body.SetMeta("instanceId", inst.Id);
            body.SetMeta("openingId", o.Id);
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            AddChild(body);

            if (o.Id == _selectedOpeningId && _selectedIds.Contains(inst.Id))
                AddChild(new MeshInstance3D
                {
                    Mesh = MeshBuilder.Box(size),
                    Transform = world,
                    MaterialOverride = PlaceholderMaterial(),
                });
        }
    }

    /// <summary>
    /// Super Corgi Ball motion preview: a translucent copy of a moving piece at the end of its travel
    /// (s = 1), plus a small cube on the hinge when it swings — edit-time only, never baked.
    /// </summary>
    private void AddMotionGhost(PrimitiveInstanceData inst, IPrimitive prim, ArrayMesh mesh, Transform3D xform)
    {
        if (prim is MarkerPrimitive || !Motion.IsMoving(inst)) return;

        AddChild(new MeshInstance3D
        {
            Mesh = mesh,
            Transform = xform * Motion.PoseAt(inst, 1f),
            MaterialOverride = GhostMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        float angle = inst.Parameters.ContainsKey("m_angle") ? inst.Parameters["m_angle"].AsSingle() : 0f;
        if (!Mathf.IsZeroApprox(angle))
            AddChild(new MeshInstance3D
            {
                Mesh = MeshBuilder.Box(new Vector3(0.25f, 0.25f, 0.25f)),
                Transform = xform * new Transform3D(Basis.Identity, Motion.Hinge(inst)),
                MaterialOverride = HingeMaterial(),
            });
    }

    private static StandardMaterial3D GhostMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0.3f, 0.95f, 1.0f, 0.25f),
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static StandardMaterial3D HingeMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(1.0f, 0.35f, 0.75f),
        NoDepthTest = true,
    };

    /// <summary>A copy of the wall sharing its parameters but with one opening removed (for the intact-wall view).</summary>
    private static PrimitiveInstanceData WithoutOpening(PrimitiveInstanceData inst, string openingId)
    {
        var clone = new PrimitiveInstanceData
        {
            Id = inst.Id,
            PrimitiveType = inst.PrimitiveType,
            LocalTransform = inst.LocalTransform,
            Parameters = inst.Parameters,
            MaterialSlots = inst.MaterialSlots,
        };
        foreach (OpeningData o in inst.Openings)
            if (o.Id != openingId) clone.Openings.Add(o);
        return clone;
    }

    private static float GetF(PrimitiveInstanceData d, string key, float def)
        => d.Parameters.ContainsKey(key) ? d.Parameters[key].AsSingle() : def;

    private static StaticBody3D BuildPickBody(PrimitiveInstanceData inst, IPrimitive prim, BuildContext ctx, Transform3D xform)
    {
        var body = new StaticBody3D { Transform = xform };
        body.SetMeta("instanceId", inst.Id);
        foreach (Shape3D shape in prim.BuildCollision(inst, ctx))
            body.AddChild(new CollisionShape3D { Shape = shape });
        return body;
    }

    // Translucent so that, used as a MaterialOverlay, the underlying texture shows through the orange tint.
    private static StandardMaterial3D HighlightMaterial() => new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(1.0f, 0.62f, 0.22f, 0.35f),
        EmissionEnabled = true,
        Emission = new Color(0.85f, 0.45f, 0.12f),
        EmissionEnergyMultiplier = 0.5f,
    };

    /// <summary>Solid orange block shown in place of a selected opening's hole.</summary>
    private static StandardMaterial3D PlaceholderMaterial() => new()
    {
        AlbedoColor = new Color(1.0f, 0.55f, 0.15f),
        EmissionEnabled = true,
        Emission = new Color(0.9f, 0.45f, 0.1f),
        EmissionEnergyMultiplier = 0.6f,
    };
}
