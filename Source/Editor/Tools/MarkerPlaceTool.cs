using Godot;
using Godot.Collections;
using LevelBuilder.Core;
using LevelBuilder.Core.Data;
using LevelBuilder.Editor.Grid;

namespace LevelBuilder.Editor.Tools;

/// <summary>
/// Places a gameplay marker (Start / Goal / Bone) with a single click at the hovered cell centre. Facing
/// (<c>yaw</c>) and other values are tuned afterwards in the inspector. The tool stays active so several
/// bones can be dropped in a row.
/// </summary>
public sealed class MarkerPlaceTool : DrawToolBase
{
    private readonly string _typeId;

    public MarkerPlaceTool(string typeId, string name)
    {
        _typeId = typeId;
        Name = name;
    }

    public override string Name { get; }
    public override GridSnapMode SnapMode => GridSnapMode.Cell;

    protected override void ResetState() { }

    public override void OnClick()
    {
        Vector3? at = HoveredPoint();
        if (at == null) return;
        Ctx.AddInstance(Build(at.Value));
    }

    public override void UpdatePreview()
    {
        Vector3? at = HoveredPoint();
        if (at == null) { HidePreview(); return; }

        PrimitiveInstanceData inst = Build(at.Value);
        Transform3D world = inst.LocalTransform;
        world.Origin += Ctx.ElevationOffset;
        ShowPreview(Ctx.Registry.Get(_typeId).BuildMesh(inst, Ctx.BuildCtx()), world);
    }

    // Whichever snap the user has toggled to (Tab): cell centre by default, corner when switched.
    private Vector3? HoveredPoint() => Ctx.Cursor.HoveredCell ?? Ctx.Cursor.HoveredCorner;

    private PrimitiveInstanceData Build(Vector3 at) => new()
    {
        Id = Ids.New(),
        PrimitiveType = _typeId,
        LocalTransform = new Transform3D(Basis.Identity, new Vector3(at.X, 0, at.Z)),
        Parameters = new Dictionary(),
    };
}
