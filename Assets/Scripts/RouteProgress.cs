/* RouteProgress
 * Computes progress along a RoutePath for a given Transform (e.g., the car).
 * Useful for evaluation or reward shaping (purely passive; does not enforce motion).
 * Exposes:
 *  - current segment index and t in [0,1]
 *  - distance to next waypoint
 *  - signed cross-track error (left positive)
 *  - event when passing a waypoint index */

using System;
using UnityEngine;

public class RouteProgress : MonoBehaviour
{
    public RoutePath path;
    public Transform followed; // car root (Transform to track)

    [Header("Events")]
    public event Action<int> OnPassedWaypoint;

    [Header("Debug")]
    public int segmentIndex = -1;
    public float segmentT = 0f;
    public float distanceToNext = 0f;
    public float signedCrossTrack = 0f;
    public int lastPassedWaypoint = -1;

    void Reset()
    {
        followed = transform;
    }

    void Update()
    {
        if (path == null || followed == null || path.Count < 2) return;

        Vector3 pos = followed.position;
        Vector3 closest;
        if (path.ClosestSegment(pos, out int seg, out float t, out closest))
        {
            segmentIndex = seg;
            segmentT = t;

            var a = path.GetWaypoint(seg).position;
            var b = path.GetWaypoint(seg + 1).position;
            distanceToNext = Vector3.Distance(closest, b);

            signedCrossTrack = path.SignedCrossTrack(pos, seg);

            // waypoint passage (index of the "from" node when t crosses 1.0 to next)
            if (t > 0.999f)
            {
                int wp = seg + 1;
                if (path.loop) wp = (wp % path.Count);
                if (wp != lastPassedWaypoint)
                {
                    lastPassedWaypoint = wp;
                    OnPassedWaypoint?.Invoke(wp);
                }
            }
        }
    }
}