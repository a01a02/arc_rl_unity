using UnityEngine;

public class RouteProgress
{
    private Vector3[] _pts;
    private float[] _accum;
    private float _total;
    private float _best;
    private Vector3 _lastDir = Vector3.forward;
    public float LastLateralMeters { get; private set; }

    public RouteProgress(Transform[] wps)
    {
        if (wps == null || wps.Length < 2) { _pts = null; return; }
        _pts = new Vector3[wps.Length];
        for (int i=0;i<wps.Length;i++) _pts[i] = wps[i].position;
        _accum = new float[_pts.Length];
        _accum[0] = 0f;
        for (int i=1;i<_pts.Length;i++)
            _accum[i] = _accum[i-1] + Vector3.Distance(_pts[i-1], _pts[i]);
        _total = _accum[_accum.Length-1];
        _best = 0f;
        LastLateralMeters = 0f;
    }

    public void Reset() { _best = 0f; LastLateralMeters = 0f; }

    // returns delta progress (0..1) since last best; updates LastLateralMeters and segment dir
    public float Update(Vector3 pos, out Vector3 segDir)
    {
        segDir = _lastDir;
        if (_pts == null || _pts.Length < 2 || _total <= 0f) { LastLateralMeters = 0f; return 0f; }

        float bestDist2 = float.MaxValue;
        float bestProg = 0f;
        Vector3 bestD = segDir;

        for (int i=0;i<_pts.Length-1;i++)
        {
            Vector3 a = _pts[i], b = _pts[i+1];
            Vector3 ab = b - a;
            float ab2 = ab.sqrMagnitude;
            if (ab2 < 1e-6f) continue;
            float t = Mathf.Clamp01(Vector3.Dot(pos - a, ab) / ab2);
            Vector3 proj = a + t * ab;
            float dist2 = (pos - proj).sqrMagnitude;
            if (dist2 < bestDist2)
            {
                bestDist2 = dist2;
                float along = _accum[i] + t * (_accum[i+1] - _accum[i]);
                bestProg = Mathf.Clamp01(along / _total);
                bestD = ab.normalized;
            }
        }

        _lastDir = bestD;
        segDir = bestD;
        LastLateralMeters = Mathf.Sqrt(bestDist2);

        float delta = Mathf.Max(0f, bestProg - _best);
        _best = Mathf.Max(_best, bestProg);
        return delta;
    }
}