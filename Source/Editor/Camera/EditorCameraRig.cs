using Godot;

namespace LevelBuilder.Editor.Camera;

/// <summary>
/// Turntable viewport camera, like Godot's / Blender's 3D view:
///   • Middle mouse drag        → orbit around the focus point
///   • Shift + middle mouse drag → pan the focus point
///   • Mouse wheel              → zoom (dolly toward/away from focus)
///   • Press 7                  → toggle orthographic top-down view (Blender numpad-7),
///                                 looking straight down for laying out the floor plan
///                                 (start orbiting to drop back out of it, like Blender)
///   • Press . (period/numpad .)  → frame the selection (falls back to the whole level)
///   • Press Home                 → frame the whole level
///
/// This node IS the focus pivot: its Position is the look-at target, its rotation
/// is the orbit, and the child Camera3D sits back along local +Z at <see cref="Distance"/>.
/// </summary>
public partial class EditorCameraRig : Node3D
{
    [Export] public float Distance { get; set; } = DefaultDistance;
    [Export] public float MinDistance { get; set; } = 1f;
    [Export] public float MaxDistance { get; set; } = 500f;
    [Export] public float OrbitSensitivity { get; set; } = 0.01f;
    [Export] public float PanSensitivity { get; set; } = 0.0015f;
    [Export] public float ZoomStep { get; set; } = 0.1f;

    /// <summary>
    /// World bounds to frame: called with <c>all=false</c> for the selection, <c>true</c> for the whole
    /// level; null = nothing there. Injected by Main so the camera stays free of editor-session types.
    /// </summary>
    public System.Func<bool, Aabb?> BoundsProvider { get; set; }

    private const float DefaultDistance = 18f;
    private const float FramePadding = 1.25f; // breathing room around the framed bounds

    private float _yaw = Mathf.DegToRad(-45f);
    private float _pitch = Mathf.DegToRad(-35f);
    private Camera3D _camera;
    private bool _orbiting;
    private bool _panning;
    private bool _topDown;

    // Saved perspective orbit so toggling 7 off restores the previous viewpoint.
    private float _savedYaw;
    private float _savedPitch;

    public override void _Ready()
    {
        _camera = new Camera3D { Name = "Camera3D" };
        AddChild(_camera);
        _camera.Current = true;
        Apply();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Key7 or Key.Kp7 }:
                ToggleTopDown();
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Period or Key.KpPeriod }:
                FrameSelection();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Home }:
                FrameAll();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton mb:
                HandleButton(mb);
                break;
            case InputEventMouseMotion mm when _orbiting || _panning:
                HandleMotion(mm);
                break;
        }
    }

    /// <summary>Orthographic straight-down view for floor-plan layout; toggling back restores the
    /// previous orbit. Bound to 7 here; also callable from the View menu.</summary>
    public void ToggleTopDown()
    {
        if (!_topDown)
        {
            _savedYaw = _yaw;
            _savedPitch = _pitch;
            _yaw = 0f;
            _pitch = Mathf.DegToRad(-90f); // look straight down
            _topDown = true;
        }
        else
        {
            _yaw = _savedYaw;
            _pitch = _savedPitch;
            _topDown = false;
        }
        Apply();
    }

    /// <summary>Frames the selection; with nothing selected, the whole level (then the origin).</summary>
    public void FrameSelection() => Frame(BoundsProvider?.Invoke(false) ?? BoundsProvider?.Invoke(true));

    /// <summary>Frames every object in the level (or resets to the origin when the level is empty).</summary>
    public void FrameAll() => Frame(BoundsProvider?.Invoke(true));

    /// <summary>
    /// Re-centres the orbit pivot on <paramref name="bounds"/> and pulls the camera back until it fits,
    /// keeping the current view direction. Top-down frames the XZ footprint (Distance drives the
    /// orthographic Size there); perspective fits the bounding sphere in the vertical FOV.
    /// </summary>
    private void Frame(Aabb? bounds)
    {
        if (bounds == null)
        {
            Position = Vector3.Zero;
            Distance = DefaultDistance;
            Apply();
            return;
        }

        Aabb box = bounds.Value;
        Position = box.GetCenter();
        float distance;
        if (_topDown)
        {
            // Ortho Size is the vertical extent; pad both footprint axes so wide aspect + tall both fit,
            // and keep the camera above the box's top.
            distance = Mathf.Max(Mathf.Max(box.Size.X, box.Size.Z) * FramePadding, box.Size.Y);
        }
        else
        {
            float radius = box.Size.Length() * 0.5f;
            distance = radius * FramePadding / Mathf.Sin(Mathf.DegToRad(_camera.Fov * 0.5f));
        }
        Distance = Mathf.Clamp(distance, Mathf.Max(MinDistance, 2f), MaxDistance);
        Apply();
    }

    /// <summary>Leaves the orthographic top-down lock into a free perspective orbit, keeping the current
    /// (straight-down) heading rather than restoring the pre-top-down viewpoint — so an orbit drag tilts
    /// out of the floor-plan view from where you're looking. Pitch is pulled inside the orbit clamp so the
    /// first drag tilts smoothly instead of snapping off the −90° edge.</summary>
    private void ExitTopDownIntoOrbit()
    {
        _topDown = false;
        _pitch = Mathf.Clamp(_pitch, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));
        Apply();
    }

    private void HandleButton(InputEventMouseButton mb)
    {
        switch (mb.ButtonIndex)
        {
            case MouseButton.WheelUp when mb.Pressed:
                Zoom(-1);
                break;
            case MouseButton.WheelDown when mb.Pressed:
                Zoom(1);
                break;
            case MouseButton.Middle:
                _panning = mb.Pressed && mb.ShiftPressed;
                _orbiting = mb.Pressed && !mb.ShiftPressed;
                // Blender numpad-7 behaviour: orbiting drops you out of the top-down lock and keeps
                // turning from the current straight-down orientation (panning stays in top-down).
                if (_orbiting && _topDown) ExitTopDownIntoOrbit();
                break;
        }
    }

    private void HandleMotion(InputEventMouseMotion mm)
    {
        if (_orbiting && !_topDown)
        {
            _yaw -= mm.Relative.X * OrbitSensitivity;
            _pitch -= mm.Relative.Y * OrbitSensitivity;
            _pitch = Mathf.Clamp(_pitch, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));
            Apply();
        }
        else if (_panning)
        {
            // Pan in the camera's screen plane; scale by distance so it feels constant on screen.
            Basis camBasis = _camera.GlobalTransform.Basis;
            float scale = PanSensitivity * Distance;
            Position += (-camBasis.X * mm.Relative.X + camBasis.Y * mm.Relative.Y) * scale;
        }
    }

    private void Zoom(int dir)
    {
        float factor = 1f + dir * ZoomStep; // in (dir -1) → 0.9, out (dir +1) → 1.1
        Distance = Mathf.Clamp(Distance * factor, MinDistance, MaxDistance);
        Apply();
    }

    private void Apply()
    {
        // Default Euler order (YXZ) gives turntable orbit: yaw about global Y, pitch about local X.
        Rotation = new Vector3(_pitch, _yaw, 0);
        _camera.Position = new Vector3(0, 0, Distance);

        // Top-down uses an orthographic projection (no perspective foreshortening), so the
        // floor plan reads true-to-scale like a blueprint. Size tracks Distance so the wheel
        // still zooms. Perspective everywhere else.
        if (_topDown)
        {
            _camera.Projection = Camera3D.ProjectionType.Orthogonal;
            _camera.Size = Distance;
        }
        else
        {
            _camera.Projection = Camera3D.ProjectionType.Perspective;
        }
    }
}
