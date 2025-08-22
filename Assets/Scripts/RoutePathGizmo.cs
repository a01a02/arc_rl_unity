// Assets/Scripts/RoutePathGizmo.cs
using UnityEngine;
[ExecuteAlways]
public class RoutePathGizmo : MonoBehaviour
{
    public Color lineColor = Color.cyan;
    public float sphere = 0.15f;
    void OnDrawGizmos()
    {
        var rp = GetComponent<RoutePath>();
        if (rp == null || rp.waypoints == null || rp.waypoints.Length < 2) return;
        Gizmos.color = lineColor;
        for (int i = 0; i < rp.waypoints.Length; i++)
        {
            var t = rp.waypoints[i]; if (!t) continue;
            Gizmos.DrawSphere(t.position, sphere);
            if (i < rp.waypoints.Length - 1 && rp.waypoints[i+1])
                Gizmos.DrawLine(t.position, rp.waypoints[i+1].position);
        }
    }
}