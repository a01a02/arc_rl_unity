using UnityEngine;
using System;

[DisallowMultipleComponent]
public class GoalProximity : MonoBehaviour
{
    [Header("Refs")]
    public Transform carRoot;
    public Transform goalCenter;
    [Tooltip("Distance in meters for success.")]
    public float radius = 3f;

    [Header("Reset gating")]
    [Tooltip("If true, goal will only trigger after the car first exits the radius post-reset.")]
    public bool armAfterExit = true;

    // Armed state (prevents instant-success when spawning inside the goal)
    private bool _armed = true;

    // Events used by RLClientSender (already subscribes)
    public event Action OnGoalInside;
    public event Action OnGoalReached;

    private RLClientSender _sender;

    void Awake()
    {
        // Subscribe to episode resets so we can re-arm
        _sender = FindObjectOfType<RLClientSender>();
        if (_sender != null) _sender.OnEpisodeReset += HandleEpisodeReset;
    }

    void OnDestroy()
    {
        if (_sender != null) _sender.OnEpisodeReset -= HandleEpisodeReset;
    }

    public void ResetForNewEpisode()
    {
        HandleEpisodeReset();
    }

    private void HandleEpisodeReset()
    {
        // Start disarmed if gating enabled; otherwise legacy behavior
        _armed = !armAfterExit;
    }

    void Update()
    {
        if (carRoot == null || goalCenter == null) return;

        float c, s, d;
        if (!ComputeGoalTelemetry(out c, out s, out d)) return;

        bool inside = d <= radius;

        // Arm once we have exited the bubble after a reset
        if (armAfterExit && !_armed)
        {
            if (!inside) _armed = true;
            return;
        }

        if (inside)
        {
            OnGoalInside?.Invoke();
            OnGoalReached?.Invoke();
        }
    }

    // -------- Public API expected by other components --------

    // NEW (current) API used by RLClientSender and others
    public bool GetDistances(out float cos, out float sin, out float distXZ)
    {
        return ComputeGoalTelemetry(out cos, out sin, out distXZ);
    }

    // COMPAT overload for older code (e.g., TelemetryHUD)
    // Allows calling GetDistances(out cos, out sin) without the distance.
    public bool GetDistances(out float cos, out float sin)
    {
        float d;
        return ComputeGoalTelemetry(out cos, out sin, out d);
    }

    // Check if an arbitrary world position is inside the goal radius
    public bool IsInside(Vector3 worldPos)
    {
        var center = (goalCenter != null) ? goalCenter.position : transform.position;
		float r = radius;
		Vector3 flat = new Vector3(worldPos.x - center.x, 0f, worldPos.z - center.z);
		return flat.sqrMagnitude <= r * r;
    }

    // Internal shared computation
    private bool ComputeGoalTelemetry(out float cos, out float sin, out float distXZ)
    {
        cos = 0f; sin = 0f; distXZ = -1f;
        if (carRoot == null || goalCenter == null) return false;

        Vector3 p = carRoot.position;
        Vector3 g = goalCenter.position;

        Vector2 fwd = new Vector2(carRoot.forward.x, carRoot.forward.z);
        Vector2 dir = new Vector2(g.x - p.x, g.z - p.z);
        distXZ = dir.magnitude;

        if (distXZ <= 1e-6f)
        {
            cos = 1f; sin = 0f; distXZ = 0f;
            return true;
        }

        fwd = fwd.normalized;
        dir = dir.normalized;

        float c = Vector2.Dot(fwd, dir);
        float cross = fwd.x * dir.y - fwd.y * dir.x; // z-component in 2D
        float ang = Mathf.Atan2(cross, c);

        sin = Mathf.Sin(ang);
        cos = Mathf.Cos(ang);
        return true;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (goalCenter == null) return;
        Gizmos.color = new Color(0f, 0.6f, 1f, 0.25f);
        Gizmos.DrawSphere(goalCenter.position, 0.2f);
        UnityEditor.Handles.color = new Color(0f, 0.6f, 1f, 0.5f);
        UnityEditor.Handles.DrawWireDisc(goalCenter.position, Vector3.up, radius);
    }
#endif
}