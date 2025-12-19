using System.Collections.Generic;
using UnityEngine;

public class LearnedPathVisualizer : MonoBehaviour
{
    [Header("References")]
    public Transform carRoot;
    
    [Header("Visualization")]
    public bool showWaypoints = true;
    public bool showPathLine = true;
    public Color waypointColor = Color.green;
    public Color pathColor = Color.yellow;
    public float waypointRadius = 0.15f;
    
    // Thread-safe waypoint storage
    private readonly object _waypointLock = new object();
    private float[] _pendingWaypoints = null;
    private int _pendingCount = 0;
    
    // Main thread visualization
    private List<Vector3> waypoints = new List<Vector3>();
    private LineRenderer lineRenderer;
    
    void Start()
    {
        // Create line renderer for path
        GameObject lineObj = new GameObject("WaypointPath");
        lineObj.transform.parent = transform;
        lineRenderer = lineObj.AddComponent<LineRenderer>();
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.startColor = pathColor;
        lineRenderer.endColor = pathColor;
        lineRenderer.startWidth = 0.05f;
        lineRenderer.endWidth = 0.05f;
        lineRenderer.enabled = showPathLine;
    }
    
    /// <summary>
    /// Thread-safe method called from network thread
    /// Stores waypoints for processing on main thread
    /// </summary>
    public void UpdateWaypoints(float[] waypointData, int count)
    {
        if (waypointData == null || count <= 0) return;
        
        // Thread-safe storage of waypoint data
        lock (_waypointLock)
        {
            _pendingWaypoints = new float[waypointData.Length];
            System.Array.Copy(waypointData, _pendingWaypoints, waypointData.Length);
            _pendingCount = count;
        }
    }
    
    void Update()
    {
        // Process pending waypoints on main thread
        ProcessPendingWaypoints();
        
        // Update line renderer visibility
        if (lineRenderer != null)
        {
            lineRenderer.enabled = showPathLine && waypoints.Count > 1;
        }
    }
    
    /// <summary>
    /// Process waypoints that were received from network thread
    /// This runs on the main thread so Transform operations are safe
    /// </summary>
    private void ProcessPendingWaypoints()
    {
        float[] dataToProcess = null;
        int countToProcess = 0;
        
        // Quickly grab pending data
        lock (_waypointLock)
        {
            if (_pendingWaypoints != null && _pendingCount > 0)
            {
                dataToProcess = _pendingWaypoints;
                countToProcess = _pendingCount;
                _pendingWaypoints = null;
                _pendingCount = 0;
            }
        }
        
        // Process on main thread
        if (dataToProcess != null && countToProcess > 0 && carRoot != null)
        {
            waypoints.Clear();
            
            for (int i = 0; i < countToProcess; i++)
            {
                if (i * 2 + 1 < dataToProcess.Length)
                {
                    float localX = dataToProcess[i * 2];
                    float localZ = dataToProcess[i * 2 + 1];
                    
                    // Convert from car-relative to world coordinates
                    Vector3 localPoint = new Vector3(localX, 0, localZ);
                    Vector3 worldPoint = carRoot.TransformPoint(localPoint);
                    waypoints.Add(worldPoint);
                }
            }
            
            UpdateLineRenderer();
        }
    }
    
    private void UpdateLineRenderer()
    {
        if (lineRenderer != null && waypoints.Count > 1)
        {
            lineRenderer.positionCount = waypoints.Count;
            lineRenderer.SetPositions(waypoints.ToArray());
        }
    }
    
    void OnDrawGizmos()
    {
        if (!showWaypoints) return;
        
        Gizmos.color = waypointColor;
        
        // Draw waypoint spheres
        foreach (var wp in waypoints)
        {
            Gizmos.DrawSphere(wp, waypointRadius);
        }
        
        // Draw path line in editor
        if (showPathLine && waypoints.Count > 1)
        {
            Gizmos.color = pathColor;
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Gizmos.DrawLine(waypoints[i], waypoints[i + 1]);
            }
        }
    }
    
    /// <summary>
    /// Clear all waypoints
    /// </summary>
    public void Clear()
    {
        lock (_waypointLock)
        {
            _pendingWaypoints = null;
            _pendingCount = 0;
        }
        waypoints.Clear();
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = 0;
        }
    }
    
    /// <summary>
    /// Called when episode resets
    /// </summary>
    public void OnEpisodeReset()
    {
        Clear();
    }
}