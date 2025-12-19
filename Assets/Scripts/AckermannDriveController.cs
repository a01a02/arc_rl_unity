/*
 * AckermannDriveController
 * ------------------------
 * Ackermann steering model using direct Rigidbody physics (no WheelColliders).
 * 
 * This combines:
 *  - Ackermann steering geometry (correct turn radius calculation)
 *  - Direct physics forces like DoubleTrack (reliable, no WheelCollider issues)
 *  - Simple, tunable parameters for 1/10 scale RC cars
 *
 * Drop-in replacement API:
 *  - SetInputs(steer, throttle, brake)
 *  - ResetVehicle()
 *  - Speed, YawRate, SteerAngleDeg properties
 *
 * Inputs: steer [-1..1], throttle [0..1], brake [0..1]
 */

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class AckermannDriveController : MonoBehaviour
{
    // ---------- Inputs (written by RL bridge) ----------
    [Header("Inputs (read-only)")]
    [Range(-1f, 1f)] public float steerCmd = 0f;
    [Range(0f, 1f)]  public float throttleCmd = 0f;
    [Range(0f, 1f)]  public float brakeCmd = 0f;

    // ---------- Geometry ----------
    [Header("Geometry")]
    [Tooltip("Wheelbase (m): distance between front and rear axles.")]
    public float wheelbase = 0.28f;
    
    [Tooltip("Track width (m): distance between left and right wheels.")]
    public float trackWidth = 0.18f;

    // ---------- Mass ----------
    [Header("Mass & Inertia")]
    public float mass = 1.7f;
    [Tooltip("Yaw inertia (kg*m^2)")]
    public float yawInertia = 0.05f;

    // ---------- Steering ----------
    [Header("Steering")]
    [Tooltip("Max steer angle (deg) at |steerCmd|=1.")]
    public float maxSteerDeg = 30f;
    
    [Tooltip("Steering slew rate (deg/s).")]
    public float steerSlewDegPerSec = 540f;

    // ---------- Drive ----------
    [Header("Drive")]
    [Tooltip("Max drive force (N) at throttle=1.")]
    public float driveForceMax = 12f;
    
    [Tooltip("Max brake force (N) at brake=1.")]
    public float brakeForceMax = 20f;
    
    [Tooltip("Rolling resistance force (N).")]
    public float rollingResistance = 0.5f;
    
    [Tooltip("Aerodynamic drag coefficient.")]
    public float dragCoeff = 0.3f;

    // ---------- Safety ----------
    [Header("Safety & Caps")]
    [Tooltip("Hard speed cap (m/s).")]
    public float maxSpeed = 5.0f;
    
    [Tooltip("Minimum speed for steering to take effect (m/s).")]
    public float minSpeedForSteering = 0.1f;

    // ---------- Physics Options ----------
    [Header("Physics")]
    [Tooltip("Disable gravity for top-down sims.")]
    public bool disableGravity = true;
    
    [Tooltip("Freeze X/Z rotation (keep car upright).")]
    public bool freezeUpright = true;

    // ---------- Debug ----------
    [Header("Debug")]
    public bool debugLog = false;
    public bool drawDebugLines = false;

    // ---- Internal State ----
    private Rigidbody rb;
    private float currentSteerAngle = 0f;  // degrees, after slew
    private float throttleActual = 0f;
    private float brakeActual = 0f;

    // ---- Public Telemetry ----
    public float Speed => rb != null ? rb.linearVelocity.magnitude : 0f;
    public float ForwardSpeed => rb != null ? Vector3.Dot(rb.linearVelocity, transform.forward) : 0f;
    public float YawRate => rb != null ? rb.angularVelocity.y * Mathf.Rad2Deg : 0f;
    public float SteerAngleDeg => currentSteerAngle;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = mass;
        rb.useGravity = !disableGravity;
        rb.linearDamping = 0f;
        rb.angularDamping = 0.5f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        
        // Set inertia tensor for realistic yaw behavior
        rb.inertiaTensor = new Vector3(1f, yawInertia, 1f);
        rb.inertiaTensorRotation = Quaternion.identity;

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
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        steerCmd = 0f;
        throttleCmd = 0f;
        brakeCmd = 0f;
        currentSteerAngle = 0f;
        throttleActual = 0f;
        brakeActual = 0f;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        // --- 1) Actuator dynamics ---
        UpdateActuators(dt);

        // --- 2) Calculate forces ---
        float forwardSpeed = ForwardSpeed;
        float speed = Speed;

        // Drive force (rear wheel drive)
        float driveForce = throttleActual * driveForceMax;

        // Brake force (opposes motion)
        float brakeForce = brakeActual * brakeForceMax;
        
        // Resistance forces
        float resistanceForce = rollingResistance + dragCoeff * speed * speed;

        // Net longitudinal force
        float netForce = driveForce - resistanceForce;
        
        // Apply braking
        if (speed > 0.01f)
        {
            netForce -= brakeForce * Mathf.Sign(forwardSpeed);
        }

        // Apply drive force along car's forward direction
        Vector3 forceWorld = transform.forward * netForce;
        rb.AddForce(forceWorld, ForceMode.Force);

        // --- 3) Ackermann steering (yaw rate based on bicycle model) ---
        if (speed > minSpeedForSteering && Mathf.Abs(currentSteerAngle) > 0.1f)
        {
            // Bicycle model: turnRadius = wheelbase / tan(steerAngle)
            float steerRad = currentSteerAngle * Mathf.Deg2Rad;
            float turnRadius = wheelbase / Mathf.Tan(Mathf.Abs(steerRad));
            
            // Prevent division issues
            turnRadius = Mathf.Max(turnRadius, 0.1f);
            
            // Angular velocity = v / r
            float targetYawRate = forwardSpeed / turnRadius;
            
            // Apply sign based on steering direction
            if (steerRad < 0) targetYawRate = -targetYawRate;
            
            // Convert to angular velocity (rad/s)
            Vector3 targetAngVel = new Vector3(0f, targetYawRate, 0f);
            
            // Blend current angular velocity toward target
            Vector3 currentAngVel = rb.angularVelocity;
            Vector3 newAngVel = Vector3.Lerp(currentAngVel, targetAngVel, 10f * dt);
            newAngVel.x = currentAngVel.x;  // Preserve other axes
            newAngVel.z = currentAngVel.z;
            rb.angularVelocity = newAngVel;
        }
        else if (speed <= minSpeedForSteering)
        {
            // At very low speed, allow some rotation for turning in place
            if (Mathf.Abs(currentSteerAngle) > 0.1f && throttleActual > 0.1f)
            {
                float turnRate = Mathf.Sign(currentSteerAngle) * 0.5f;
                rb.angularVelocity = new Vector3(0f, turnRate, 0f);
            }
        }

        // --- 4) Speed limiting ---
        if (speed > maxSpeed)
        {
            rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;
        }

        // --- 5) Debug ---
        if (debugLog && Time.frameCount % 30 == 0)
        {
            Debug.Log($"[Ackermann] speed={speed:F2} steer={currentSteerAngle:F1}° " +
                      $"throttle={throttleActual:F2} yawRate={YawRate:F1}°/s");
        }

        if (drawDebugLines)
        {
            Debug.DrawRay(transform.position, transform.forward * 0.5f, Color.blue);
            Debug.DrawRay(transform.position, rb.linearVelocity.normalized * 0.3f, Color.green);
        }
    }

    private void UpdateActuators(float dt)
    {
        // Steering slew rate
        float targetSteer = steerCmd * maxSteerDeg;
        float maxSteerStep = steerSlewDegPerSec * dt;
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetSteer, maxSteerStep);

        // Throttle/brake lag (simple first-order)
        float throttleLag = 8f;
        float brakeLag = 12f;
        throttleActual = Mathf.Lerp(throttleActual, throttleCmd, throttleLag * dt);
        brakeActual = Mathf.Lerp(brakeActual, brakeCmd, brakeLag * dt);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        // Draw wheelbase
        Gizmos.color = Color.yellow;
        Vector3 front = transform.position + transform.forward * (wheelbase / 2f);
        Vector3 rear = transform.position - transform.forward * (wheelbase / 2f);
        Gizmos.DrawLine(front, rear);

        // Draw track width
        Gizmos.color = Color.cyan;
        Vector3 left = -transform.right * (trackWidth / 2f);
        Vector3 right = transform.right * (trackWidth / 2f);
        Gizmos.DrawLine(front + left, front + right);
        Gizmos.DrawLine(rear + left, rear + right);

        // Draw steering arc
        if (Application.isPlaying && Mathf.Abs(currentSteerAngle) > 1f)
        {
            float steerRad = currentSteerAngle * Mathf.Deg2Rad;
            float turnRadius = wheelbase / Mathf.Tan(Mathf.Abs(steerRad));
            
            Gizmos.color = Color.green;
            Vector3 turnCenter = transform.position + transform.right * turnRadius * Mathf.Sign(steerRad);
            Gizmos.DrawWireSphere(turnCenter, 0.1f);
        }
    }
#endif
}