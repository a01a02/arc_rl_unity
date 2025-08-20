using UnityEngine;

/// <summary>
/// Minimal continuous controller for a 4-wheeled car using WheelColliders.
/// Exposes normalized steer/throttle and a collision flag.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SimpleCarController : MonoBehaviour
{
    [Header("Wheels")]
    public WheelCollider wheelFL;
    public WheelCollider wheelFR;
    public WheelCollider wheelRL;
    public WheelCollider wheelRR;

    [Header("Visuals (optional)")]
    public Transform visualFL;
    public Transform visualFR;
    public Transform visualRL;
    public Transform visualRR;

    [Header("Params")]
    public float maxSteerAngle = 30f;
    public float maxMotorTorque = 150f;
    public float brakeTorque = 300f;

    [Header("Reset")]
    public Transform spawnPoint;

    private Rigidbody _rb;
    private float _currentSteer;     // degrees
    private float _currentTorque;    // Nm
    private float _steerNorm;        // [-1,1]
    private bool _collisionFlag = false;

    public float CurrentSteerNorm => _steerNorm;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        // Optional stability
        _rb.centerOfMass += new Vector3(0f, -0.3f, 0.2f);
    }

    void FixedUpdate()
    {
        ApplyToWheels();
        UpdateVisuals();
    }

    public void SetInputs(float steerNorm, float throttleNorm)
    {
        _steerNorm = Mathf.Clamp(steerNorm, -1f, 1f);
        float targetSteer = _steerNorm * maxSteerAngle;
        float targetTorque = Mathf.Clamp01(throttleNorm) * maxMotorTorque;

        // Small smoothing
        _currentSteer = Mathf.Lerp(_currentSteer, targetSteer, 0.2f);
        _currentTorque = Mathf.Lerp(_currentTorque, targetTorque, 0.2f);
    }

    private void ApplyToWheels()
    {
        // Steering on front
        wheelFL.steerAngle = _currentSteer;
        wheelFR.steerAngle = _currentSteer;

        // Torque on rear (or AWD if you prefer)
        wheelRL.motorTorque = _currentTorque;
        wheelRR.motorTorque = _currentTorque;

        // Simple automatic braking when torque very low
        float brake = (_currentTorque < 0.05f) ? brakeTorque * 0.2f : 0f;
        wheelFL.brakeTorque = brake;
        wheelFR.brakeTorque = brake;
        wheelRL.brakeTorque = brake;
        wheelRR.brakeTorque = brake;
    }

    private void UpdateVisuals()
    {
        UpdateWheelPose(wheelFL, visualFL);
        UpdateWheelPose(wheelFR, visualFR);
        UpdateWheelPose(wheelRL, visualRL);
        UpdateWheelPose(wheelRR, visualRR);
    }

    private void UpdateWheelPose(WheelCollider col, Transform visual)
    {
        if (col == null || visual == null) return;
        Vector3 pos; Quaternion rot;
        col.GetWorldPose(out pos, out rot);
        visual.SetPositionAndRotation(pos, rot);
    }

    public void ResetVehicle()
    {
        if (spawnPoint != null)
        {
            transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);
        }
        _currentSteer = 0f;
        _currentTorque = 0f;
        _steerNorm = 0f;
        _collisionFlag = false;
    }

    public bool ConsumeCollisionFlag()
    {
        bool f = _collisionFlag;
        _collisionFlag = false;
        return f;
    }

    void OnCollisionEnter(Collision c)
    {
        // Flag "meaningful" collisions; adjust threshold as needed
        if (c.relativeVelocity.magnitude > 2.0f)
            _collisionFlag = true;
    }
}