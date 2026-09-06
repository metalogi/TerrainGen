using UnityEngine;
using UnityEngine.InputSystem;
using Sonoma.Core.CoordinateSpace;
using Sonoma.Core.Quadtree;

/// Topology-aware fly camera using the new Input System.
///
/// Auto-speed scales with elevation above the topology base surface, so the camera flies
/// faster when high above terrain and slower near the ground. The mouse wheel modifies a
/// persistent multiplier (clamped) on top of the auto-speed. The camera up vector is kept
/// aligned to the local topology up — radial outward on a sphere, inward (toward axis) on a
/// cylinder ringworld, world up on a plane.
///
/// Controls: WASD = move, Q/E = down/up along topology up, Shift = sprint, RMB = look,
///           Mouse wheel = adjust speed multiplier within [WheelMultMin, WheelMultMax].
public class TopologyFlyCamera : MonoBehaviour
{
    [Header("Topology Source")]
    [Tooltip("Reads Topology + radii from this manager. Auto-found in Start if null. Falls back to flat plane (Y up) if none in scene.")]
    public QuadtreeManager TerrainManager;

    [Header("Auto Speed (Elevation-driven)")]
    [Tooltip("Base move speed at zero elevation (units/sec).")]
    public float BaseSpeed = 8f;
    [Tooltip("Extra speed added per metre of elevation above the base surface (units/sec per metre).")]
    public float SpeedPerMetre = 0.5f;
    public float MinSpeed = 4f;
    public float MaxSpeed = 800f;
    public float SprintMultiplier = 3f;

    [Header("Mouse Wheel Multiplier")]
    [Tooltip("Lowest the wheel can pull the auto-speed.")]
    public float WheelMultMin = 0.25f;
    [Tooltip("Highest the wheel can push the auto-speed.")]
    public float WheelMultMax = 4f;
    [Tooltip("Geometric step applied per scroll tick.")]
    public float WheelStep = 1.15f;

    [Header("Look")]
    public float LookSensitivity = 0.15f;
    [Tooltip("Hold right mouse to look. If false, look is always active and the cursor is locked.")]
    public bool RequireRightMouseToLook = true;

    InputAction _move, _look, _wheel, _sprint, _lookEnable;
    float _wheelMult = 1f;

    void OnEnable()
    {
        _move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector3");
        _move.AddCompositeBinding("3DVector")
            .With("Forward",  "<Keyboard>/w")
            .With("Backward", "<Keyboard>/s")
            .With("Left",     "<Keyboard>/a")
            .With("Right",    "<Keyboard>/d")
            .With("Up",       "<Keyboard>/e")
            .With("Down",     "<Keyboard>/q");

        _look       = new InputAction("Look",       InputActionType.Value,  binding: "<Mouse>/delta",       expectedControlType: "Vector2");
        _wheel      = new InputAction("Wheel",      InputActionType.Value,  binding: "<Mouse>/scroll/y",    expectedControlType: "Axis");
        _sprint     = new InputAction("Sprint",     InputActionType.Button, binding: "<Keyboard>/leftShift");
        _lookEnable = new InputAction("LookEnable", InputActionType.Button, binding: "<Mouse>/rightButton");

        _move.Enable();
        _look.Enable();
        _wheel.Enable();
        _sprint.Enable();
        _lookEnable.Enable();
    }

    void OnDisable()
    {
        _move?.Dispose();
        _look?.Dispose();
        _wheel?.Dispose();
        _sprint?.Dispose();
        _lookEnable?.Dispose();
    }

    void Start()
    {
        if (TerrainManager == null)
            TerrainManager = FindFirstObjectByType<QuadtreeManager>();
    }

    void Update()
    {
        bool lookActive = !RequireRightMouseToLook || _lookEnable.IsPressed();
        Cursor.lockState = lookActive ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !lookActive;

        Vector3 topoUp    = ComputeTopoUp(transform.position);
        float   elevation = ComputeElevation(transform.position);

        float wheel = _wheel.ReadValue<float>();
        if (wheel >  0.001f) _wheelMult = Mathf.Clamp(_wheelMult * WheelStep, WheelMultMin, WheelMultMax);
        if (wheel < -0.001f) _wheelMult = Mathf.Clamp(_wheelMult / WheelStep, WheelMultMin, WheelMultMax);

        if (lookActive)
        {
            Vector2 md = _look.ReadValue<Vector2>();
            if (md.sqrMagnitude > 1e-6f) ApplyLook(md, topoUp);
        }

        // Always realign to topoUp — keeps the camera oriented as it crosses curvature even
        // when the user isn't actively looking.
        AlignUp(topoUp);

        float autoSpeed = Mathf.Clamp(BaseSpeed + Mathf.Max(0f, elevation) * SpeedPerMetre, MinSpeed, MaxSpeed);
        float speed     = autoSpeed * _wheelMult * (_sprint.IsPressed() ? SprintMultiplier : 1f);

        Vector3 mv = _move.ReadValue<Vector3>();
        if (mv.sqrMagnitude > 0f)
        {
            Vector3 worldMove = transform.right   * mv.x
                              + topoUp            * mv.y
                              + transform.forward * mv.z;
            transform.position += worldMove * speed * Time.deltaTime;
        }
    }

    // Yaw rotates the tangent-projected forward around topoUp; pitch tilts the result
    // toward/away from topoUp. The output is always exactly aligned with topoUp — no
    // gimbal lock, no roll drift even on curved topologies where "up" varies with position.
    void ApplyLook(Vector2 mouseDelta, Vector3 topoUp)
    {
        Vector3 fwd        = transform.forward;
        Vector3 fwdTangent = Vector3.ProjectOnPlane(fwd, topoUp);
        if (fwdTangent.sqrMagnitude < 1e-6f)
        {
            fwdTangent = Vector3.ProjectOnPlane(transform.right, topoUp);
            if (fwdTangent.sqrMagnitude < 1e-6f) fwdTangent = Vector3.Cross(topoUp, Vector3.right);
        }
        fwdTangent.Normalize();

        float pitchDeg = Mathf.Asin(Mathf.Clamp(Vector3.Dot(fwd, topoUp), -1f, 1f)) * Mathf.Rad2Deg;

        float yawDelta = mouseDelta.x * LookSensitivity;
        fwdTangent = Quaternion.AngleAxis(yawDelta, topoUp) * fwdTangent;
        pitchDeg   = Mathf.Clamp(pitchDeg + mouseDelta.y * LookSensitivity, -89f, 89f);

        float pr      = pitchDeg * Mathf.Deg2Rad;
        Vector3 newFwd = Mathf.Cos(pr) * fwdTangent + Mathf.Sin(pr) * topoUp;
        transform.rotation = Quaternion.LookRotation(newFwd, topoUp);
    }

    void AlignUp(Vector3 topoUp)
    {
        Vector3 fwd        = transform.forward;
        Vector3 fwdTangent = Vector3.ProjectOnPlane(fwd, topoUp);
        if (fwdTangent.sqrMagnitude < 1e-6f) return;
        fwdTangent.Normalize();
        float pitchDeg = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(Vector3.Dot(fwd, topoUp), -1f, 1f)) * Mathf.Rad2Deg, -89f, 89f);
        float pr       = pitchDeg * Mathf.Deg2Rad;
        Vector3 newFwd = Mathf.Cos(pr) * fwdTangent + Mathf.Sin(pr) * topoUp;
        transform.rotation = Quaternion.LookRotation(newFwd, topoUp);
    }

    // Convert render-space (camera transform) to absolute world by adding back the
    // floating-origin offset. Sphere/cylinder geometry is anchored at absolute (0,0,0).
    Vector3 RenderToAbsoluteWorld(Vector3 renderPos)
    {
        var o = WorldOriginSystem.WorldOrigin;
        return renderPos + new Vector3((float)o.x, (float)o.y, (float)o.z);
    }

    Vector3 ComputeTopoUp(Vector3 renderPos)
    {
        if (TerrainManager == null) return Vector3.up;
        Vector3 abs = RenderToAbsoluteWorld(renderPos);
        switch (TerrainManager.Topology)
        {
            case WorldTopology.UVSphere:
                return abs.sqrMagnitude < 1e-6f ? Vector3.up : abs.normalized;
            case WorldTopology.Cylinder:
            {
                Vector3 radial = new Vector3(abs.x, abs.y, 0f);
                if (radial.sqrMagnitude < 1e-6f) return Vector3.up;
                // Terrain is on the inside of the cylinder; "up" is toward the axis.
                return -radial.normalized;
            }
            default:
                return Vector3.up;
        }
    }

    float ComputeElevation(Vector3 renderPos)
    {
        if (TerrainManager == null) return renderPos.y;
        Vector3 abs = RenderToAbsoluteWorld(renderPos);
        switch (TerrainManager.Topology)
        {
            case WorldTopology.UVSphere:
                return abs.magnitude - TerrainManager.SphereRadius;
            case WorldTopology.Cylinder:
                return TerrainManager.CylRadius - new Vector2(abs.x, abs.y).magnitude;
            default:
                return abs.y;
        }
    }
}
