using Godot;

namespace LevelBuilder.Editor.Grid;

/// <summary>
/// Infinite editor reference grid on the XZ plane (like Godot's 3D editor grid):
/// minor lines every <see cref="CellSize"/>, brighter major lines every
/// <see cref="MajorEvery"/> cells, coloured X/Z axis lines through the origin, and a
/// distance fade so it never shows a hard edge.
///
/// Implementation: a single large quad that <b>follows the camera in X/Z</b> every frame,
/// with a fragment shader that draws the lines analytically from world coordinates. The
/// lines stay locked to world space (only the carrier quad moves), so the grid is
/// effectively infinite and works arbitrarily far from the origin — no level-size cap.
/// Set this node's Y position to the active storey's elevation; a fainter "ghost" grid is
/// drawn at world y=0 for ground reference whenever the edit plane is off the ground.
/// </summary>
public partial class GridRenderer : Node3D
{
    [Export] public float CellSize { get; set; } = 1.0f;
    /// <summary>A major (brighter) line is drawn every N cells.</summary>
    [Export] public int MajorEvery { get; set; } = 10;

    private static readonly Color MinorColor = new(0.30f, 0.30f, 0.33f, 0.35f);
    private static readonly Color MajorColor = new(0.55f, 0.55f, 0.60f, 0.55f);
    private static readonly Color XAxisColor = new(0.80f, 0.30f, 0.32f, 0.85f);
    private static readonly Color ZAxisColor = new(0.30f, 0.45f, 0.82f, 0.85f);

    /// <summary>Half-size of the carrier quad, metres. Large enough that the distance fade
    /// always completes well inside it, so the quad's edge is never visible.</summary>
    private const float QuadRadius = 6000f;
    /// <summary>Ghost grid alpha multiplier relative to the main grid.</summary>
    private const float GhostOpacity = 0.08f;

    private MeshInstance3D _main;
    private MeshInstance3D _ghost;
    private ShaderMaterial _mainMat;
    private ShaderMaterial _ghostMat;

    public override void _Ready()
    {
        Shader shader = new() { Code = ShaderCode };

        _mainMat = MakeMaterial(shader, opacity: 1.0f);
        _main = MakeQuad("GridMain", _mainMat);
        AddChild(_main);

        _ghostMat = MakeMaterial(shader, opacity: GhostOpacity);
        _ghost = MakeQuad("GridGhost", _ghostMat);
        AddChild(_ghost);

        PushParams();
    }

    /// <summary>Pushes <see cref="CellSize"/>/<see cref="MajorEvery"/> + the palette to both
    /// materials. Call after changing the cell size at runtime.</summary>
    public void Rebuild() => PushParams();

    private void PushParams()
    {
        if (_mainMat == null) return;
        foreach (ShaderMaterial m in new[] { _mainMat, _ghostMat })
        {
            m.SetShaderParameter("cell_size", CellSize);
            m.SetShaderParameter("major_every", (float)Mathf.Max(1, MajorEvery));
            m.SetShaderParameter("minor_color", MinorColor);
            m.SetShaderParameter("major_color", MajorColor);
            m.SetShaderParameter("x_axis_color", XAxisColor);
            m.SetShaderParameter("z_axis_color", ZAxisColor);
        }
    }

    public override void _Process(double delta)
    {
        Camera3D cam = GetViewport()?.GetCamera3D();
        if (cam == null) return;

        Vector3 camPos = cam.GlobalPosition;
        float elevation = Position.Y;

        // Coverage follows the camera in X/Z; fade radius scales with how far the camera is
        // from the edit plane, so zoomed in the grid fades close (no shimmering haze) and
        // zoomed out it reaches far. Coverage (QuadRadius) is fixed and large enough that the
        // fade always finishes inside the quad — its edge never shows.
        float planeDist = Mathf.Abs(camPos.Y - elevation);
        float fadeEnd = Mathf.Clamp(planeDist * 9f + 30f, 60f, QuadRadius * 0.9f);
        float fadeStart = fadeEnd * 0.55f;

        UpdateQuad(_main, _mainMat, new Vector3(camPos.X, elevation, camPos.Z), fadeStart, fadeEnd);

        // Ghost sits on the ground (world y=0); hide it when the edit plane already is the ground.
        bool showGhost = Mathf.Abs(elevation) > 0.01f;
        _ghost.Visible = showGhost;
        if (showGhost)
            UpdateQuad(_ghost, _ghostMat, new Vector3(camPos.X, 0f, camPos.Z), fadeStart, fadeEnd);
    }

    private static void UpdateQuad(MeshInstance3D quad, ShaderMaterial mat, Vector3 worldPos, float fadeStart, float fadeEnd)
    {
        quad.GlobalPosition = worldPos;
        mat.SetShaderParameter("fade_start", fadeStart);
        mat.SetShaderParameter("fade_end", fadeEnd);
    }

    private static MeshInstance3D MakeQuad(string name, ShaderMaterial mat) => new()
    {
        Name = name,
        Mesh = new PlaneMesh { Size = new Vector2(QuadRadius * 2f, QuadRadius * 2f) },
        MaterialOverride = mat,
        // The grid is decoration: don't let it cast/receive shadows or get picked up by GI.
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        GIMode = GeometryInstance3D.GIModeEnum.Disabled,
    };

    private static ShaderMaterial MakeMaterial(Shader shader, float opacity)
    {
        var m = new ShaderMaterial { Shader = shader };
        m.SetShaderParameter("opacity", opacity);
        return m;
    }

    // Spatial shader: draws an anti-aliased, distance-faded grid from world XZ. Minor and major
    // lines fade out as their spacing drops below a pixel (kills moire when zoomed far out). The
    // strongest feature wins per fragment: axes over major over minor.
    private const string ShaderCode = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;

        uniform float cell_size = 1.0;
        uniform float major_every = 10.0;
        uniform vec4 minor_color : source_color;
        uniform vec4 major_color : source_color;
        uniform vec4 x_axis_color : source_color;
        uniform vec4 z_axis_color : source_color;
        uniform float fade_start = 40.0;
        uniform float fade_end = 120.0;
        uniform float opacity = 1.0;

        varying vec3 wpos;

        void vertex() {
            wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }

        // Anti-aliased coverage of a unit-spaced 2D line lattice (0 = empty, 1 = on a line).
        float lattice(vec2 coord) {
            vec2 d = fwidth(coord);
            vec2 g = abs(fract(coord - 0.5) - 0.5) / max(d, vec2(1e-6));
            return 1.0 - clamp(min(g.x, g.y), 0.0, 1.0);
        }

        // Coverage of a single axis line at coord == 0.
        float axis(float coord) {
            float d = fwidth(coord);
            return 1.0 - clamp(abs(coord) / max(d, 1e-6), 0.0, 1.0);
        }

        // Fades lines whose on-screen spacing has dropped below ~a pixel (anti-moire).
        float density_fade(vec2 coord) {
            float dens = max(fwidth(coord.x), fwidth(coord.y));
            return 1.0 - smoothstep(0.5, 2.0, dens);
        }

        void fragment() {
            vec2 p = wpos.xz;

            vec2 minor_uv = p / cell_size;
            float minor = lattice(minor_uv) * density_fade(minor_uv);

            vec2 major_uv = p / (cell_size * major_every);
            float major = lattice(major_uv) * density_fade(major_uv);

            // Strongest feature wins (premultiplied alpha): minor < major < axes.
            vec3 rgb = minor_color.rgb;
            float a = minor_color.a * minor;

            float ma = major_color.a * major;
            if (ma > a) { rgb = major_color.rgb; a = ma; }

            float za = z_axis_color.a * axis(p.x); // line along Z, at x = 0
            if (za > a) { rgb = z_axis_color.rgb; a = za; }
            float xa = x_axis_color.a * axis(p.y); // line along X, at z = 0
            if (xa > a) { rgb = x_axis_color.rgb; a = xa; }

            float fade = 1.0 - smoothstep(fade_start, fade_end, distance(p, CAMERA_POSITION_WORLD.xz));

            ALBEDO = rgb;
            ALPHA = a * fade * opacity;
        }
        """;
}
