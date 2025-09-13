/* RoutePathGizmo
 * Editor-only helper to draw waypoint indices and arrows along a RoutePath.
 * Has no runtime effect. Safe to remove in builds. */

using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(RoutePath))]
public class RoutePathGizmo : MonoBehaviour
{
    public Color indexColor = Color.white;
    public Color arrowColor = new Color(0.9f, 0.9f, 0.1f, 1f);
    public float arrowSize = 0.5f;
    public bool drawIndices = true;
    public bool drawArrows = true;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        var rp = GetComponent<RoutePath>();
        if (rp == null || rp.waypoints == null) return;

        var cam = Camera.current;
        if (cam == null) return;

        int n = rp.waypoints.Length;
        if (n == 0) return;

        if (drawIndices)
        {
            UnityEditor.Handles.color = indexColor;
            for (int i = 0; i < n; i++)
            {
                var t = rp.waypoints[i];
                if (t == null) continue;
                UnityEditor.Handles.Label(t.position + Vector3.up * 0.2f, i.ToString());
            }
        }

        if (drawArrows && n >= 2)
        {
            Gizmos.color = arrowColor;
            int last = rp.loop ? n : (n - 1);
            for (int i = 0; i < last; i++)
            {
                var a = rp.waypoints[i];
                var b = rp.waypoints[(i + 1) % n];
                if (a == null || b == null) continue;
                Vector3 mid = Vector3.Lerp(a.position, b.position, 0.5f);
                Vector3 dir = (b.position - a.position).normalized;
                DrawArrow(mid, dir, arrowSize);
            }
        }
    }

    void DrawArrow(Vector3 pos, Vector3 dir, float size)
    {
        Vector3 right = Quaternion.AngleAxis(25f, Vector3.up) * -dir;
        Vector3 left = Quaternion.AngleAxis(-25f, Vector3.up) * -dir;
        Gizmos.DrawLine(pos, pos + dir * size);
        Gizmos.DrawLine(pos + dir * size, pos + dir * size + right * (size * 0.4f));
        Gizmos.DrawLine(pos + dir * size, pos + dir * size + left * (size * 0.4f));
    }
#endif
}