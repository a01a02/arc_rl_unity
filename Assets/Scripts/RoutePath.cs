/* RoutePath
 * Holds a sequence of waypoints that define a road centerline or a route.
 * Purely for visualization/evaluation/reward shaping — it does not drive the car.
 * Usage:
 *  - Create an empty GameObject "RoutePath" and add this script.
 *  - Populate `waypoints` with Transforms (ordered).
 *  - Optionally set `loop` to connect end → start.
 * Helpers:
 *  - Closest point & segment index
 *  - Progress along the path (0..N-1 plus t in segment)
 *  - Signed cross-track error in the local (segment) frame */

using UnityEngine;

public class RoutePath : MonoBehaviour
{
    [Tooltip("Ordered points defining the path (drag Transforms here).")]
    public Transform[] waypoints;

    [Tooltip("Connect last waypoint back to the first.")]
    public bool loop = false;

    [Tooltip("Gizmo color for editor view.")]
    public Color gizmoColor = new Color(0.1f, 1f, 0.6f, 1f);

    public int Count => (waypoints != null) ? waypoints.Length : 0;

    public Transform GetWaypoint(int i)
    {
        if (Count == 0) return null;
        if (loop) i = Mod(i, Count);
        else i = Mathf.Clamp(i, 0, Count - 1);
        return waypoints[i];
    }

    // Returns closest segment index i (segment i goes from i to i+1) and interpolation t in [0,1]
    public bool ClosestSegment(Vector3 pos, out int segIndex, out float t, out Vector3 closest)
    {
        segIndex = -1;
        t = 0f;
        closest = pos;

        int n = Count;
        if (n < 2) return false;

        float bestDistSqr = float.MaxValue;
        int last = loop ? n : (n - 1);

        for (int i = 0; i < last; i++)
        {
            Vector3 a = waypoints[i].position;
            Vector3 b = waypoints[(i + 1) % n].position;
            Vector3 ab = b - a;
            float ab2 = ab.sqrMagnitude;
            if (ab2 < 1e-6f) continue;

            float u = Vector3.Dot(pos - a, ab) / ab2;
            float uClamped = Mathf.Clamp01(u);
            Vector3 p = a + ab * uClamped;
            float d2 = (pos - p).sqrMagnitude;

            if (d2 < bestDistSqr)
            {
                bestDistSqr = d2;
                segIndex = i;
                t = uClamped;
                closest = p;
            }
        }
        return segIndex >= 0;
    }

    // Signed cross-track error (left positive), relative to segment frame.
    public float SignedCrossTrack(Vector3 pos, int segIndex)
    {
        int n = Count;
        if (n < 2 || segIndex < 0) return 0f;

        Vector3 a = waypoints[segIndex].position;
        Vector3 b = waypoints[(segIndex + 1) % n].position;
        Vector3 ab = (b - a);
        ab.y = 0f;
        Vector3 right = Vector3.Cross(Vector3.up, ab.normalized);
        Vector3 ap = pos - a;
        return Vector3.Dot(ap, right);
    }

    private static int Mod(int a, int m) => (a % m + m) % m;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (waypoints == null || waypoints.Length < 2) return;
        Gizmos.color = gizmoColor;

        int n = waypoints.Length;
        int last = loop ? n : (n - 1);
        for (int i = 0; i < last; i++)
        {
            var a = waypoints[i];
            var b = waypoints[(i + 1) % n];
            if (a != null && b != null)
            {
                Gizmos.DrawSphere(a.position, 0.1f);
                Gizmos.DrawLine(a.position, b.position);
            }
        }
        // Draw last point sphere if not looped
        if (!loop && waypoints[n - 1] != null)
            Gizmos.DrawSphere(waypoints[n - 1].position, 0.1f);
    }
#endif
}