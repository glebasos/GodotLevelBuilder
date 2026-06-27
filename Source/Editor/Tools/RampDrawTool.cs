using Godot;
using Godot.Collections;
using LevelBuilder.Core;
using LevelBuilder.Core.Data;
using LevelBuilder.Editor.Grid;

namespace LevelBuilder.Editor.Tools;

/// <summary>
/// Draws a ramp between two grid corners: first click sets the bottom (front) end, second click the
/// top (back) end — the run goes start→end. Rise defaults to the active storey's height (so the ramp
/// reaches the next floor) and width to a sensible default; both are adjustable afterwards via gizmos.
/// </summary>
public sealed class RampDrawTool : DrawToolBase
{
    private const float MinLength = 0.001f;

    public override string Name => "Ramp";
    public override GridSnapMode SnapMode => GridSnapMode.Corner;

    private Vector3? _start;

    protected override void ResetState() => _start = null;

    public override void OnClick()
    {
        Vector3? corner = Ctx.Cursor.HoveredCorner;
        if (corner == null) return;
        if (_start == null) { _start = corner; return; }

        PrimitiveInstanceData ramp = BuildRamp(_start.Value, corner.Value);
        if (ramp != null) Ctx.AddInstance(ramp);
        _start = null;
        HidePreview();
    }

    public override void UpdatePreview()
    {
        if (_start == null) { HidePreview(); return; }
        if (Ctx.Cursor.HoveredCorner == null) return;

        PrimitiveInstanceData inst = BuildRamp(_start.Value, Ctx.Cursor.HoveredCorner.Value);
        if (inst == null) { HidePreview(); return; }

        Transform3D world = inst.LocalTransform;
        world.Origin += Ctx.ElevationOffset;
        ShowPreview(Ctx.Registry.Get("ramp").BuildMesh(inst, Ctx.BuildCtx()), world);
    }

    private PrimitiveInstanceData BuildRamp(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        d.Y = 0;
        float length = d.Length();
        if (length < MinLength) return null;

        float width = Ctx.Document.Grid.CellSize; // one cell wide → edges land on grid lines
        float angle = Mathf.Atan2(-d.Z, d.X);     // rotate local +X onto the run direction
        var basis = new Basis(Vector3.Up, angle);
        var mid = new Vector3((a.X + b.X) * 0.5f, 0, (a.Z + b.Z) * 0.5f);
        mid = AnchorWidth(mid, basis, width); // line = near edge, or centreline when WidthFromCenter is on

        return new PrimitiveInstanceData
        {
            Id = Ids.New(),
            PrimitiveType = "ramp",
            LocalTransform = new Transform3D(basis, mid),
            Parameters = new Dictionary
            {
                { "length", (double)length },
                { "rise", (double)Ctx.DefaultStoreyHeight },
                { "width", (double)width },
            },
        };
    }
}
