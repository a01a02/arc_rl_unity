/* RouteProgress (with Goal Fallback)
 * Computes progress along a RoutePath for a given Transform (e.g., the car).
 * If no RoutePath is present, optional goal-fallback provides usable telemetry.
 *
 * Exposes (both modes):
 *  - LateralErrorMeters  (signed; left +)
 *  - HeadingErrorRad     (signed; left +)
 *  - PathCurvature       (signed 1/m; left-turn +)
 *  - DeltaSPerStep       (meters advanced since last frame)
 *
 * Path mode (when RoutePath valid, Count>=2):
 *  - segmentIndex, segmentT, distanceToNext, signedCrossTrack, waypoint events
 *
 * Goal-fallback mode (when enabled and path invalid):
 *  - Interprets an infinite centerline through the goal with direction from goal to car
 *  - LateralErrorMeters: signed distance from that line (left of line is +)
 *  - HeadingErrorRad: signed angle (left +) between car forward (XZ) and line direction
 *  - PathCurvature: 0
 *  - DeltaSPerStep: decrease in goal distance (positive when moving toward goal)
 */

using System;
using UnityEngine;
using UnityEngine.Events;

public class RouteProgress : MonoBehaviour
{
    [Header("Inputs (Path Mode)")]
    public RoutePath path;
    public Transform followed; // car root (Transform to track)

    [Header("Goal Fallback (used when no valid path)")]
    public bool useGoalFallback = true;
    public Transform goal;          // target/goal Transform
    public Rigidbody carBodyForFB;  // optional, only used for future extensions

    // C# event for code subscribers (path mode only)
    public event Action<int> OnPassedWaypoint;

    [Header("Events (Inspector)")]
    public UnityEvent<int> OnPassedWaypointUnity;

    [Header("Debug (read-only, Path Mode)")]
    public int segmentIndex = -1;
    public float segmentT = 0f;
    public float distanceToNext = 0f;
    public float signedCrossTrack = 0f;
    public int lastPassedWaypoint = -1;

    // ===== Telemetry properties (both modes) =====
    public float LateralErrorMeters  { get; private set; } = 0f;  // left +
    public float HeadingErrorRad     { get; private set; } = 0f;  // left +
    public float PathCurvature       { get; private set; } = 0f;  // 1/m (signed)
    public float DeltaSPerStep       { get; private set; } = 0f;  // m

    // ==== Internal (path mode) ====
    private int _cachedCount = -1;
    private bool _cachedLoop = false;
    private float[] _segLen = Array.Empty<float>();    // per-segment length
    private float[] _cumLen = Array.Empty<float>();    // arclength at segment start
    private float _totalLen = 0f;

    // Last-frame arclength for ds computation (path mode)
    private float _lastS = 0f;
    private bool _lastSValid = false;

    // ==== Internal (goal-fallback mode) ====
    private float _lastGoalDist = 0f;  // distance to goal in XZ
    private bool _fbHaveLast = false;

    void Reset()
    {
        followed = transform;
    }

    void OnEnable()
    {
        RebuildLengthTableIfNeeded(force: true);
        _lastSValid = false;
        _fbHaveLast = false;
        DeltaSPerStep = 0f;
    }

    void Update()
    {
        if (followed == null) return;

        bool pathValid = (path != null && path.Count >= 2);

        if (pathValid)
        {
            UpdatePathMode();
        }
        else if (useGoalFallback && goal != null)
        {
            UpdateGoalFallbackMode();
        }
        // else: leave zeros
    }

    // ======================== Path Mode ========================

    private void UpdatePathMode()
    {
        // If the path topology changed, rebuild lengths/cumulative arclength.
        RebuildLengthTableIfNeeded(force: false);

        Vector3 pos = followed.position;
        if (!path.ClosestSegment(pos, out int seg, out float t, out Vector3 closest))
            return;

        segmentIndex = seg;
        segmentT = Mathf.Clamp01(t);

        var a = path.GetWaypoint(seg).position;
        var b = path.GetWaypoint(seg + 1).position;
        distanceToNext = Vector3.Distance(closest, b);

        // Cross-track (signed, left +)
        signedCrossTrack = path.SignedCrossTrack(pos, seg);
        LateralErrorMeters = signedCrossTrack;

        // Heading error (signed, left +): compare car forward (XZ) vs. path tangent (XZ)
        Vector2 tHat = DirXZ(b - a);             // path tangent on this segment
        Vector2 fwd  = DirXZ(followed.forward);  // car forward projected onto XZ
        float dot = Vector2.Dot(tHat, fwd);
        float crz = CrossZ(tHat, fwd);           // z-component of 2D cross
        HeadingErrorRad = Mathf.Atan2(crz, Mathf.Clamp(dot, -1f, 1f));

        // Signed curvature (1/m) using 3-point XZ estimate near current location
        PathCurvature = EstimateCurvatureXZ(seg, segmentT);

        // Arclength now and delta since last frame (handle loops)
        float sNow = ArclengthAt(seg, segmentT);
        if (_lastSValid)
        {
            float ds = sNow - _lastS;

            // If looped and s wrapped around, add totalLen
            if (path.loop)
            {
                if (ds < -0.5f * _totalLen) ds += _totalLen;
                if (ds < 0f && Mathf.Abs(ds) < 1e-4f) ds = 0f; // tiny negative jitter -> 0
            }
            else
            {
                if (ds < 0f) ds = 0f; // non-loop: no backward progress
            }

            DeltaSPerStep = ds;
        }
        else
        {
            DeltaSPerStep = 0f;
            _lastSValid = true;
        }
        _lastS = sNow;

        // Waypoint passage event (index of the "to" node when t ~ 1.0)
        if (segmentT > 0.999f)
        {
            int wp = seg + 1;
            if (path.loop) wp = (wp % path.Count);
            if (wp != lastPassedWaypoint)
            {
                lastPassedWaypoint = wp;
                OnPassedWaypoint?.Invoke(wp);
                OnPassedWaypointUnity?.Invoke(wp);
            }
        }
    }

    // ======================== Goal Fallback Mode ========================

    private void UpdateGoalFallbackMode()
    {
        // Direction from goal -> car (XZ centerline direction)
        Vector3 p3 = followed.position;
        Vector3 g3 = goal.position;

        Vector2 p = new Vector2(p3.x, p3.z);
        Vector2 g = new Vector2(g3.x, g3.z);
        Vector2 v = p - g;

        float dist = v.magnitude;               // distance to goal in XZ
        Vector2 dir = (dist > 1e-6f) ? (v / dist) : new Vector2(1f, 0f); // unit from goal to car

        // Signed lateral error: point-to-infinite-line through goal with direction 'dir'
        // sign: left of the line is +
        // distance = cross(dir, (p - g)) since dir is unit -> meters
        float latSigned = CrossZ(dir, v);
        LateralErrorMeters = latSigned;

        // Heading error: compare car forward (XZ) vs. centerline direction
        Vector2 fwd = DirXZ(followed.forward);
        float dot = Vector2.Dot(dir, fwd);
        float crz = CrossZ(dir, fwd);
        HeadingErrorRad = Mathf.Atan2(crz, Mathf.Clamp(dot, -1f, 1f));

        // No defined curvature along a straight centerline toward goal
        PathCurvature = 0f;

        // Progress toward goal: positive when distance to goal decreases
        if (_fbHaveLast)
            DeltaSPerStep = Mathf.Max(0f, _lastGoalDist - dist);  // clamp tiny negatives
        else
            DeltaSPerStep = 0f;

        _lastGoalDist = dist;
        _fbHaveLast = true;

        // Keep path-mode debug fields neutral
        segmentIndex = -1;
        segmentT = 0f;
        distanceToNext = dist;   // treat goal as "next"
        signedCrossTrack = latSigned;
    }

    // ======================== Helpers ========================

    private void RebuildLengthTableIfNeeded(bool force)
    {
        if (path == null) return;

        bool need = force || _cachedCount != path.Count || _cachedLoop != path.loop;
        if (!need) return;

        int N = Mathf.Max(2, path.Count);
        _segLen = new float[N - 1 + (path.loop ? 1 : 0)];
        _cumLen = new float[_segLen.Length];
        _totalLen = 0f;

        for (int i = 0; i < _segLen.Length; i++)
        {
            int ia = i;
            int ib = (i + 1);
            if (path.loop)
            {
                ia = i % N;
                ib = (i + 1) % N;
            }
            Vector3 A = path.GetWaypoint(ia).position;
            Vector3 B = path.GetWaypoint(ib).position;
            float L = Vector3.Distance(A, B);
            _segLen[i] = Mathf.Max(1e-6f, L);
            _cumLen[i] = _totalLen;
            _totalLen += _segLen[i];
        }

        _cachedCount = path.Count;
        _cachedLoop = path.loop;
        _lastSValid = false; // arclength reference invalidated
        DeltaSPerStep = 0f;
    }

    private static Vector2 DirXZ(Vector3 v)
    {
        Vector2 d = new Vector2(v.x, v.z);
        float m = d.magnitude;
        return (m > 1e-6f) ? (d / m) : new Vector2(1f, 0f);
    }

    private static float CrossZ(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    private float ArclengthAt(int seg, float t01)
    {
        if (_segLen == null || _segLen.Length == 0) return 0f;

        // Map RoutePath segment index to our local segment index.
        int localSeg = seg;
        if (!path.loop)
        {
            // RoutePath seg is 0..Count-2; our table matches that.
            localSeg = Mathf.Clamp(seg, 0, _segLen.Length - 1);
        }
        else
        {
            int N = Mathf.Max(2, path.Count);
            localSeg = ((seg % N) + N) % N; // wrap positive
        }

        float t = Mathf.Clamp01(t01);
        float s = _cumLen[localSeg] + t * _segLen[localSeg];
        return s;
    }

    private float EstimateCurvatureXZ(int seg, float t)
    {
        // Sample three nearby points along the path in XZ to estimate signed curvature.
        // If path is straight in the neighborhood, returns ~0.
        float h = 0.5f; // meters along arclength for sampling window (tune to map scale)
        Vector3 Pm = SamplePositionByArclengthOffset(seg, t, -h);
        Vector3 P0 = SamplePositionByArclengthOffset(seg, t,  0f);
        Vector3 Pp = SamplePositionByArclengthOffset(seg, t,  h);

        Vector2 A = new Vector2(Pm.x, Pm.z);
        Vector2 B = new Vector2(P0.x, P0.z);
        Vector2 C = new Vector2(Pp.x, Pp.z);

        float ab = (A - B).magnitude;
        float bc = (B - C).magnitude;
        float ca = (C - A).magnitude;

        float denom = ab * bc * ca;
        if (denom < 1e-6f) return 0f;

        // Twice signed area of triangle ABC (2A = cross(AB, AC))
        Vector2 AB = B - A;
        Vector2 AC = C - A;
        float twoAreaSigned = CrossZ(AB, AC);

        // Curvature kappa = 4A / (|AB||BC||CA|) with sign from orientation
        float kappa = (2f * twoAreaSigned) / denom; // (since twoAreaSigned = 2A_signed)
        if (Mathf.Abs(kappa) < 1e-6f) kappa = 0f;
        return kappa;
    }

    private Vector3 SamplePositionByArclengthOffset(int seg, float t, float ds)
    {
        if (_segLen == null || _segLen.Length == 0 || path == null || path.Count < 2)
            return (path != null) ? path.GetWaypoint(Mathf.Clamp(seg, 0, path.Count - 1)).position : transform.position;

        float s = ArclengthAt(seg, t) + ds;

        // Wrap or clamp s into valid range
        if (path.loop)
        {
            s = Mod(s, _totalLen);
        }
        else
        {
            s = Mathf.Clamp(s, 0f, Mathf.Max(1e-6f, _totalLen - 1e-6f));
        }

        // Find segment idx such that _cumLen[idx] <= s < _cumLen[idx] + _segLen[idx]
        int idx = FindSegmentForArclength(s);
        float sLocal = s - _cumLen[idx];
        float u = Mathf.Clamp01(_segLen[idx] > 1e-6f ? (sLocal / _segLen[idx]) : 0f);

        int ia = idx;
        int ib = path.loop ? ((idx + 1) % path.Count) : Mathf.Min(idx + 1, path.Count - 1);

        Vector3 A = path.GetWaypoint(ia).position;
        Vector3 B = path.GetWaypoint(ib).position;
        return Vector3.LerpUnclamped(A, B, u);
    }

    private int FindSegmentForArclength(float s)
    {
        int last = _segLen.Length - 1;
        for (int i = 0; i < last; i++)
        {
            float s0 = _cumLen[i];
            float s1 = s0 + _segLen[i];
            if (s >= s0 && s < s1) return i;
        }
        return last;
    }

    private static float Mod(float x, float m)
    {
        if (m <= 0f) return 0f;
        float r = x % m;
        return (r < 0f) ? (r + m) : r;
    }
}