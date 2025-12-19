/*
 * AdvancedDoubleTrackController
 * -----------------------------
 * Research-grade lightweight double-track (axle) model with:
 *  - 3-DoF planar dynamics (vx, vy, r) on a Rigidbody
 *  - Steering slew + actuator lags
 *  - Front/Rear lateral tire forces from slip angles (linear cornering stiffness)
 *  - Longitudinal drive/brake forces with simple frictions & friction circle clipping
 *  - Load transfer (longitudinal & lateral) influencing axle normal loads
 *  - Optional gravity disable & upright constraints for top-down scenes
 *
 * Inputs from RLClientSender: SetInputs(steer [-1..1], throttle [0..1], brake [0..1])
 *
 * Notes:
 * - Keep fixedDeltaTime small (e.g., 0.02) for stability.
 * - This is a clean middle-ground: realistic enough for RL, still stable & fast in Unity.
 */

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class AdvancedDoubleTrackController : MonoBehaviour
{
    // ---------- Inputs (written by RL bridge) ----------
    [Header("Inputs (read-only)")]
    [Range(-1f, 1f)] public float steerCmd = 0f;     // unitless, left negative (Unity left-handed Yaw+ -> left turn)
    [Range(0f, 1f)]  public float throttleCmd = 0f;  // [0,1]
    [Range(0f, 1f)]  public float brakeCmd = 0f;     // [0,1]

    // ---------- Geometry ----------
    [Header("Geometry")]
    [Tooltip("Wheelbase (m): distance between front and rear axles.")]
    public float wheelbase = 0.28f;
    [Tooltip("CG height above ground (m). Use small values for top-down flat worlds.")]
    public float cgHeight = 0.03f;
    [Tooltip("Track width (m): distance left-right between tire centers.")]
    public float trackWidth = 0.18f;
    [Tooltip("Distance CG -> front axle (m). rearDist = wheelbase - frontDist.")]
    public float cgToFront = 0.14f;

    // ---------- Mass & inertia ----------
    [Header("Mass & Inertia")]
    public float mass = 1.7f;                    // kg
    [Tooltip("Yaw inertia (about up axis), kg*m^2")]
    public float Iz = 0.05f;

    // ---------- Tires (linear region) ----------
    [Header("Tire/Lateral")]
    [Tooltip("Front axle cornering stiffness (N/rad).")]
    public float Cf = 20.0f;
    [Tooltip("Rear axle cornering stiffness (N/rad).")]
    public float Cr = 24.0f;
    [Tooltip("Peak friction coefficient (road).")]
    public float mu = 1.1f;

    // ---------- Longitudinal ----------
    [Header("Longitudinal")]
    [Tooltip("Max drive force at wheels (N) when throttle=1.")]
    public float driveForceMax = 12.0f;
    [Tooltip("Max brake force magnitude (N) when brake=1 (shared across axles).")]
    public float brakeForceMax = 20.0f;
    [Tooltip("Aero/viscous drag coefficient (N*s/m) acting on forward speed.")]
    public float viscousDrag = 0.6f;
    [Tooltip("Rolling drag (N) roughly constant with speed (applied forward).")]
    public float rollingDrag = 0.6f;
    [Tooltip("Fraction of drive sent to rear axle (1.0 = RWD, 0.0 = FWD).")]
    [Range(0f, 1f)] public float driveRearFrac = 1.0f; // default RWD

    // ---------- Steering & actuator dynamics ----------
    [Header("Actuators")]
    [Tooltip("Max steer angle (deg) at |steerCmd|=1.")]
    public float maxSteerDeg = 35f;
    [Tooltip("Steering slew rate (deg/s).")]
    public float steerSlewDegPerSec = 540f;
    [Tooltip("Simple first-order lag for throttle (1/s). 0 = no lag.")]
    public float throttleLagRate = 8f;
    [Tooltip("Simple first-order lag for brake (1/s). 0 = no lag.")]
    public float brakeLagRate = 12f;

    // ---------- Safety caps ----------
    [Header("Safety & Caps")]
    [Tooltip("Hard speed cap (m/s).")]
    public float maxSpeed = 16.0f;
    [Tooltip("Clamp for extremely low forward speeds to avoid singularities.")]
    public float minForwardForSlip = 0.05f;

    // ---------- Physics options ----------
    [Header("Physics")]
    [Tooltip("Disable gravity for top-down sims.")]
    public bool disableGravity = true;
    [Tooltip("Freeze X/Z rotation (keep car upright).")]
    public bool freezeUpright = true;

    // ---------- Debug ----------
    [Header("Debug")]
    public bool drawForces = false;
    public bool debugLog = false;

    // ---- State ---
    private Rigidbody rb;
    private float steerAngleRad = 0f; // actual wheel steer angle (after slew)
    private float throttleAct = 0f;   // lagged
    private float brakeAct = 0f;      // lagged

    // Export some telemetry (read-only)
    public float Speed => rb != null ? rb.linearVelocity.magnitude : 0f;
    public float YawRate => rb != null ? rb.angularVelocity.y : 0f;
    public float SteerAngleDeg => steerAngleRad * Mathf.Rad2Deg;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = !disableGravity;
        rb.mass = mass;

        // Let Unity's inertia be overridden for better yaw dynamics
        rb.inertiaTensorRotation = Quaternion.identity;
        rb.inertiaTensor = new Vector3(1f, Iz, 1f); // yaw only matters for planar motion

        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.maxAngularVelocity = 200f;

        if (freezeUpright)
        {
            rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        }
    }

    // -------- Public API --------
    public void SetInputs(float steer, float throttle, float brake)
    {
        steerCmd = Mathf.Clamp(steer, -1f, 1f);
        throttleCmd = Mathf.Clamp01(throttle);
        brakeCmd = Mathf.Clamp01(brake);
    }

    public void ResetVehicle()
    {
        // Keep transform, zero velocities
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        steerCmd = throttleCmd = brakeCmd = 0f;
        throttleAct = brakeAct = 0f;
        steerAngleRad = 0f;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        // --- 1) Actuator dynamics ---
        // steering slew
        float targetSteer = Mathf.Deg2Rad * Mathf.Clamp(maxSteerDeg, 0f, 75f) * steerCmd;
        float maxStep = Mathf.Deg2Rad * Mathf.Abs(steerSlewDegPerSec) * dt;
        steerAngleRad = Mathf.MoveTowards(steerAngleRad, targetSteer, maxStep);

        // throttle / brake lags (first-order)
        throttleAct = FirstOrderLag(throttleAct, throttleCmd, throttleLagRate, dt);
        brakeAct    = FirstOrderLag(brakeAct,    brakeCmd,    brakeLagRate,    dt);

        // --- 2) Kinematics in body frame ---
        Vector3 vWorld = rb.linearVelocity;
        Vector3 vBody = transform.InverseTransformVector(vWorld);
        float ux = vBody.x;   // lateral
        float uz = vBody.z;   // forward
        float r  = rb.angularVelocity.y;

        // Ensure forward component small floor for slip-angle denominators
        float uForward = Mathf.Sign(uz) * Mathf.Max(Mathf.Abs(uz), minForwardForSlip);

        // Axle distances
        float Lf = Mathf.Clamp(cgToFront, 1e-4f, wheelbase - 1e-4f);
        float Lr = Mathf.Max(1e-4f, wheelbase - Lf);

        // Slip angles (radians)
        // alpha_f = atan2(v + Lf*r, u) - delta
        // alpha_r = atan2(v - Lr*r, u)
        float alpha_f = Mathf.Atan2(ux + Lf * r, uForward) - steerAngleRad;
        float alpha_r = Mathf.Atan2(ux - Lr * r, uForward);

        // --- 3) Static normal loads + load transfer ---
        float g = Physics.gravity.magnitude;
        float Fz_total = mass * g;

        // static axle split (based on CG position)
        float Fzf0 = Fz_total * (Lr / (Lf + Lr));
        float Fzr0 = Fz_total * (Lf / (Lf + Lr));

        // Longitudinal acceleration estimate from commands (pre-saturation preview)
        // Convert throttle/brake to idealized Fx requests (N)
        float Fx_req = throttleAct * driveForceMax - brakeAct * brakeForceMax * Mathf.Sign(uForward);

        // We'll distribute to axles: drive to rear/front using driveRearFrac, braking split 50/50
        float Fx_drive_front = (1f - driveRearFrac) * Mathf.Max(0f, Fx_req);
        float Fx_drive_rear  = (driveRearFrac) * Mathf.Max(0f, Fx_req);
        float Fx_brake_eachAxle = 0.5f * Mathf.Max(0f, -Fx_req);

        float Fx_front_req = Fx_drive_front - Fx_brake_eachAxle * Mathf.Sign(uForward);
        float Fx_rear_req  = Fx_drive_rear  - Fx_brake_eachAxle * Mathf.Sign(uForward);

        // Approx long accel from requested Fx (before clipping)
        float ax_preview = (Fx_front_req + Fx_rear_req) / Mathf.Max(1e-6f, mass);

        // Longitudinal load transfer ΔFz = m * ax * h / L
        float dFz_long = mass * ax_preview * cgHeight / Mathf.Max(1e-6f, wheelbase);
        float Fzf = Mathf.Max(0f, Fzf0 - dFz_long);
        float Fzr = Mathf.Max(0f, Fzr0 + dFz_long);

        // Lateral load transfer (approx from ay = r*uz + ... we use kinematic ay ≈ r*uz + dvx/dt (~0))
        float ay_est = r * uz; // small-angle approx, adequate for sim
        float dFz_lat = mass * ay_est * cgHeight / Mathf.Max(1e-6f, trackWidth);
        // Split left/right if needed; for axle aggregate, we keep totals Fzf/Fzr (already include long transfer)

        // --- 4) Lateral tire forces (linear tyre model) with friction circle clipping ---
        float Fyf_lin = -Cf * alpha_f;
        float Fyr_lin = -Cr * alpha_r;

        // Friction capacity (axle) ~ mu * Fz
        float Fy_front_cap = mu * Fzf;
        float Fy_rear_cap  = mu * Fzr;

        // Apply friction circle against combined longitudinal at each axle
        // Combined: sqrt(Fx^2 + Fy^2) <= mu * Fz
        float Fyf = ClipFrictionCircle(Fx_front_req, Fyf_lin, Fy_front_cap);
        float Fyr = ClipFrictionCircle(Fx_rear_req,  Fyr_lin,  Fy_rear_cap);

        // After clipping lateral, recompute allowed longitudinal by circle (optional).
        // Here we keep Fx requests and only clip in total at the end: safer: clip longitudinal too
        float Fx_front = ClipLongitudinalGivenLateral(Fx_front_req, Fyf, Fy_front_cap);
        float Fx_rear  = ClipLongitudinalGivenLateral(Fx_rear_req,  Fyr,  Fy_rear_cap);

        // --- 5) Resistive forces: viscous + rolling ---
        float F_visc = -viscousDrag * uz;
        float F_roll = -rollingDrag * Mathf.Sign(uz);

        // Sum longitudinal in body frame (Z-forward)
        float Fx_body = Fx_front + Fx_rear + F_visc + F_roll;

        // Sum lateral in body frame (X-right)
        float Fy_body = Fyf * Mathf.Cos(steerAngleRad) + Fyr; // small delta: lateral from front rotated by delta
        // (A more exact formulation would rotate the entire front axle force; this is good & fast.)

        // Cap speed to prevent blow-ups
        Vector3 velWorld = rb.linearVelocity;
        if (velWorld.magnitude > maxSpeed)
        {
            velWorld = velWorld.normalized * maxSpeed;
            rb.linearVelocity = velWorld;
            vBody = transform.InverseTransformVector(velWorld); // refresh
            uz = vBody.z; ux = vBody.x;
        }

        // --- 6) Apply forces & yaw moment to Rigidbody ---
        // Body → World
        Vector3 F_world = transform.TransformVector(new Vector3(Fy_body, 0f, Fx_body));
        rb.AddForce(F_world, ForceMode.Force);

        // Yaw moment about CG: Mz = Lf*Fyf - Lr*Fyr  (front lateral positive -> yaw CCW)
        float Mz = Lf * (Fyf) - Lr * (Fyr);
        // Also include small aligning effect from steering angle on front axle (optional minor term)
        // Apply as torque around up axis in world
        Vector3 T_world = new Vector3(0f, Mz, 0f);
        rb.AddTorque(T_world, ForceMode.Force);

        // --- 7) Debug draw / log ---
        if (drawForces)
        {
            Vector3 p = transform.position;
            Debug.DrawRay(p, transform.forward * (Fx_body * 0.02f), Color.green);   // long
            Debug.DrawRay(p, transform.right * (Fy_body * 0.02f),   Color.magenta); // lat
        }

        if (debugLog && Time.frameCount % 30 == 0)
        {
            Debug.Log(
                $"[ADTC] v={Speed:0.00} r={YawRate:+0.00;-0.00} " +
                $"steer={SteerAngleDeg:+0.0;-0.0}° " +
                $"Fx(f,r)=({Fx_front:+0.0;-0.0},{Fx_rear:+0.0;-0.0}) " +
                $"Fy(f,r)=({Fyf:+0.0;-0.0},{Fyr:+0.0;-0.0})"
            );
        }
    }

    // -------- Helpers --------
    private static float FirstOrderLag(float y, float u, float rate, float dt)
    {
        if (rate <= 0f) return u;
        float a = Mathf.Exp(-rate * dt);
        return a * y + (1f - a) * u;
    }

    private static float ClipFrictionCircle(float Fx_req, float Fy_lin, float Fy_cap)
    {
        // Limit lateral based on remaining capacity given requested longitudinal
        float FxAbs = Mathf.Abs(Fx_req);
        float capSq = Fy_cap * Fy_cap - FxAbs * FxAbs;
        if (capSq <= 0f) return 0f;
        float Fy_max = Mathf.Sqrt(capSq);
        return Mathf.Clamp(Fy_lin, -Fy_max, +Fy_max);
    }

    private static float ClipLongitudinalGivenLateral(float Fx_req, float Fy, float Fy_cap)
    {
        float remSq = Fy_cap * Fy_cap - Fy * Fy;
        if (remSq <= 0f) return 0f;
        float Fx_max = Mathf.Sqrt(remSq);
        return Mathf.Clamp(Fx_req, -Fx_max, +Fx_max);
    }
}