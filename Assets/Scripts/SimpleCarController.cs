/* SimpleCarController
 * Lightweight car controller using Rigidbody kinematics suitable for 1/10th scale.
 * It consumes (steer, throttle) from RLClientSender via SetInputs() and applies
 * forward force and yaw using a bicycle-like approximation. No wheel colliders required.
 * Public API:
 *  - SetInputs(float steer [-1..1], float throttle [0..1])
 *  - ResetVehicle()   // clears velocities; optional spawn
 * Notes:
 *  - This is intentionally simple and stable for RL. Tune accel/drag/maxSpeed/turnRate
 *    for your scene scale. */

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class SimpleCarController : MonoBehaviour
{
    [Header("Inputs (read-only outside)")]
    [Range(-1f, 1f)] public float steerInput = 0f;
    [Range(0f, 1f)] public float throttleInput = 0f;

    [Header("Kinematic Params")]
    public float accel = 12f; // m/s^2 per full throttle
    public float drag = 1.5f; // linear drag factor
    public float maxSpeed = 12f; // m/s
    public float turnRateDeg = 140f; // deg/s at full steer (scaled by speed)

    [Header("Reset")]
    public Transform optionalSpawn;  // if set, ResetVehicle() moves here

    private Rigidbody _rb;

    public Rigidbody Body => _rb;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ; // keep upright
        _rb.maxAngularVelocity = 100f;
    }

    public void SetInputs(float steer, float throttle)
    {
        steerInput = Mathf.Clamp(steer, -1f, 1f);
        throttleInput = Mathf.Clamp01(throttle);
    }

    public void ResetVehicle()
    {
        if (optionalSpawn != null)
        {
            _rb.position = optionalSpawn.position;
            _rb.rotation = optionalSpawn.rotation;
        }
        _rb.velocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        steerInput = 0f;
        throttleInput = 0f;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        // Forward accel + drag
        Vector3 forward = transform.forward;
        float vForward = Vector3.Dot(_rb.velocity, forward);
        float a = accel * throttleInput - drag * vForward;
        float vNew = Mathf.Clamp(vForward + a * dt, -maxSpeed, maxSpeed);

        // Compose final velocity (keep lateral velocity small for stability)
        Vector3 vel = forward * vNew;
        vel.y = _rb.velocity.y; // preserve vertical
        _rb.velocity = vel;

        // Yaw from steer — scale by speed for stability
        float speedFactor = Mathf.InverseLerp(0f, maxSpeed, Mathf.Abs(vNew));
        float yawDeg = steerInput * turnRateDeg * speedFactor;
        Quaternion dq = Quaternion.Euler(0f, yawDeg * dt, 0f);
        _rb.MoveRotation(_rb.rotation * dq);
    }
}