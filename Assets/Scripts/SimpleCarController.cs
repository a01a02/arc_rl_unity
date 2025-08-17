using UnityEngine;

public class SimpleCarController : MonoBehaviour
{
    [Header("Wheel Colliders")]
    public WheelCollider wheelFL, wheelFR, wheelRL, wheelRR;

    [Header("Wheel Meshes")]
    public Transform meshFL, meshFR, meshRL, meshRR;

    [Header("Drive Settings")]
    public float motorTorque = 1500f;
    public float maxSteeringAngle = 30f;

    [Header("Wheel Visual Correction")]
    public Vector3 visualRotationOffset = new Vector3(0, 0, 90);

    private float currentSteer = 0f;
    private float currentThrottle = 0f;
    private bool overrideControl = false;

    // Public method for RLClientSender to call
    public void SetInputs(float steering, float throttle)
    {
        currentSteer = Mathf.Clamp(steering, -1f, 1f) * maxSteeringAngle;
        currentThrottle = Mathf.Clamp(throttle, 0f, 1f) * motorTorque;
        overrideControl = true;
    }

    void FixedUpdate()
    {
        float steer, motor;

        if (overrideControl)
        {
            steer = currentSteer;
            motor = currentThrottle;
            overrideControl = false; // Reset override after applying
        }
        else
        {
            // Use Unity Input axes if no override
            steer = Input.GetAxis("Horizontal") * maxSteeringAngle;
            motor = Input.GetAxis("Vertical") * motorTorque;
        }

        // Apply steering
        wheelFL.steerAngle = steer;
        wheelFR.steerAngle = steer;

        // Apply motor torque to rear wheels
        wheelRL.motorTorque = motor;
        wheelRR.motorTorque = motor;

        // Update visual wheel meshes
        UpdateWheel(wheelFL, meshFL);
        UpdateWheel(wheelFR, meshFR);
        UpdateWheel(wheelRL, meshRL);
        UpdateWheel(wheelRR, meshRR);
    }

    void UpdateWheel(WheelCollider collider, Transform visual)
    {
        if (collider == null || visual == null) return;

        collider.GetWorldPose(out Vector3 pos, out Quaternion rot);
        visual.position = pos;
        visual.rotation = rot * Quaternion.Euler(visualRotationOffset);
    }
}