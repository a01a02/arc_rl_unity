/*
 * CollisionHandler
 * ----------------
 * Detects collisions with obstacles (buildings, walls, etc.) and signals episode termination.
 * 
 * NOT CHEATING because:
 * - Real robots have bumper sensors, lidar proximity, or IMU shock detection
 * - We're learning from consequences (crash = bad), not from privileged path info
 * 
 * Setup:
 * 1. Attach this to the car GameObject (same object as AckermannDriveController)
 * 2. Tag obstacles as "Building", "Wall", "Obstacle", or add to obstacleLayers
 * 3. Ensure car and obstacles have Colliders (car needs Rigidbody)
 * 
 * Auto-connects to RLClientSender and calls ExternalKill() on collision.
 */

using UnityEngine;
using UnityEngine.Events;

public class CollisionHandler : MonoBehaviour
{
    [Header("Detection Settings")]
    [Tooltip("Tags that count as obstacles. Leave empty to use layer mask only.")]
    public string[] obstacleTags = { "Building", "Wall", "Obstacle", "Untagged" };
    
    [Tooltip("Layers that count as obstacles (in addition to tags).")]
    public LayerMask obstacleLayers;
    
    [Tooltip("Minimum collision impulse to count as crash (filters tiny bumps).")]
    public float minCollisionImpulse = 0.5f;
    
    [Tooltip("Cooldown between collision events (seconds).")]
    public float collisionCooldown = 0.5f;

    [Header("Episode Control")]
    [Tooltip("RLClientSender to notify on collision. Auto-finds if not set.")]
    public RLClientSender rlSender;
    
    [Tooltip("Automatically end episode on collision.")]
    public bool terminateEpisode = true;

    [Header("Debug")]
    public bool debugLog = true;
    
    [Header("Events (Optional)")]
    public UnityEvent<Collision> OnCollisionDetected;
    
    // Public state
    public bool HasCollided { get; private set; } = false;
    public float LastCollisionImpulse { get; private set; } = 0f;
    public Vector3 LastCollisionPoint { get; private set; } = Vector3.zero;
    public string LastCollisionObject { get; private set; } = "";
    
    // Internal
    private float _lastCollisionTime = -999f;
    
    void OnEnable()
    {
        ResetCollisionState();
        
        // Auto-find RLClientSender if not assigned
        if (rlSender == null)
        {
            rlSender = GetComponent<RLClientSender>();
        }
        if (rlSender == null)
        {
            rlSender = GetComponentInParent<RLClientSender>();
        }
        if (rlSender == null)
        {
            rlSender = FindObjectOfType<RLClientSender>();
        }
        
        if (rlSender != null)
        {
            // Subscribe to episode reset to clear collision state
            rlSender.OnEpisodeReset += ResetCollisionState;
            Debug.Log("[CollisionHandler] Connected to RLClientSender - will terminate episode on collision");
        }
        else
        {
            Debug.LogWarning("[CollisionHandler] No RLClientSender found! Collision will be detected but episode won't end.");
        }
    }
    
    void OnDisable()
    {
        if (rlSender != null)
        {
            rlSender.OnEpisodeReset -= ResetCollisionState;
        }
    }
    
    /// <summary>
    /// Call this when episode resets to clear collision flag.
    /// </summary>
    public void ResetCollisionState()
    {
        HasCollided = false;
        LastCollisionImpulse = 0f;
        LastCollisionPoint = Vector3.zero;
        LastCollisionObject = "";
        _lastCollisionTime = -999f;
    }
    
    void OnCollisionEnter(Collision collision)
    {
        // Check cooldown
        if (Time.time - _lastCollisionTime < collisionCooldown)
            return;
            
        // Check if this is an obstacle
        if (!IsObstacle(collision.gameObject))
            return;
            
        // Check collision strength
        float impulse = collision.impulse.magnitude;
        if (impulse < minCollisionImpulse)
            return;
        
        // Record collision
        HasCollided = true;
        LastCollisionImpulse = impulse;
        LastCollisionPoint = collision.GetContact(0).point;
        LastCollisionObject = collision.gameObject.name;
        _lastCollisionTime = Time.time;
        
        if (debugLog)
        {
            Debug.Log($"[CollisionHandler] CRASH! Hit '{LastCollisionObject}' " +
                      $"impulse={impulse:F2} at {LastCollisionPoint}");
        }
        
        // Fire event for any other listeners
        OnCollisionDetected?.Invoke(collision);
        
        // CRITICAL: Terminate episode via RLClientSender
        if (terminateEpisode && rlSender != null)
        {
            Debug.Log($"[CollisionHandler] Terminating episode due to collision with '{LastCollisionObject}'");
            rlSender.ExternalKill();
        }
    }
    
    void OnCollisionStay(Collision collision)
    {
        // For continuous collision (e.g., grinding against wall)
        // Only trigger if we haven't already flagged a collision this episode
        if (HasCollided)
            return;
            
        if (!IsObstacle(collision.gameObject))
            return;
            
        // Check if we're stuck against obstacle
        float impulse = collision.impulse.magnitude;
        if (impulse >= minCollisionImpulse)
        {
            HasCollided = true;
            LastCollisionImpulse = impulse;
            LastCollisionPoint = collision.GetContact(0).point;
            LastCollisionObject = collision.gameObject.name;
            
            if (debugLog)
            {
                Debug.Log($"[CollisionHandler] STUCK against '{LastCollisionObject}'");
            }
            
            OnCollisionDetected?.Invoke(collision);
            
            // CRITICAL: Terminate episode
            if (terminateEpisode && rlSender != null)
            {
                Debug.Log($"[CollisionHandler] Terminating episode - stuck against '{LastCollisionObject}'");
                rlSender.ExternalKill();
            }
        }
    }
    
    private bool IsObstacle(GameObject obj)
    {
        // Check by tag
        if (obstacleTags != null && obstacleTags.Length > 0)
        {
            foreach (string tag in obstacleTags)
            {
                if (!string.IsNullOrEmpty(tag) && obj.CompareTag(tag))
                    return true;
            }
        }
        
        // Check by layer
        if (obstacleLayers != 0)
        {
            int objLayerMask = 1 << obj.layer;
            if ((obstacleLayers.value & objLayerMask) != 0)
                return true;
        }
        
        // Default: treat anything with a MeshCollider or BoxCollider as potential obstacle
        // (buildings typically have these)
        if (obj.GetComponent<MeshCollider>() != null || 
            obj.GetComponent<BoxCollider>() != null)
        {
            // Exclude ground/road
            if (!obj.name.ToLower().Contains("ground") && 
                !obj.name.ToLower().Contains("road") &&
                !obj.name.ToLower().Contains("floor") &&
                !obj.name.ToLower().Contains("terrain"))
            {
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Check if a specific point would collide with obstacles.
    /// Useful for predictive collision avoidance.
    /// </summary>
    public bool CheckPointCollision(Vector3 point, float radius = 0.1f)
    {
        Collider[] hits = Physics.OverlapSphere(point, radius, obstacleLayers);
        foreach (var hit in hits)
        {
            if (IsObstacle(hit.gameObject))
                return true;
        }
        return false;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (HasCollided && LastCollisionPoint != Vector3.zero)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(LastCollisionPoint, 0.2f);
        }
    }
#endif
}