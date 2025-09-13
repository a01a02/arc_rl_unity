/* LocalMap
 * Simple debug overlay to show an egocentric trail of the car's recent positions.
 * This is a purely visual aid and not used by control or rewards. */

using System.Collections.Generic;
using UnityEngine;

public class LocalMap : MonoBehaviour
{
    public Transform carRoot;
    public int maxTrailPoints = 300;
    public float sampleEveryMeters = 0.5f;
    public Color lineColor = new Color(1f, 1f, 1f, 0.9f);
    public float lineWidth = 0.03f;

    private LineRenderer _lr;
    private readonly List<Vector3> _pts = new List<Vector3>();
    private Vector3 _lastSamplePos;

    void Awake()
    {
        _lr = gameObject.AddComponent<LineRenderer>();
        _lr.material = new Material(Shader.Find("Sprites/Default"));
        _lr.startColor = _lr.endColor = lineColor;
        _lr.widthMultiplier = lineWidth;
        _lr.useWorldSpace = true;
        _lr.numCapVertices = 4;
        _lr.numCornerVertices = 4;
    }

    void Update()
    {
        if (carRoot == null) return;

        if (_pts.Count == 0 || Vector3.Distance(carRoot.position, _lastSamplePos) >= sampleEveryMeters)
        {
            _pts.Add(carRoot.position + Vector3.up * 0.02f);
            _lastSamplePos = carRoot.position;

            if (_pts.Count > maxTrailPoints)
                _pts.RemoveAt(0);

            _lr.positionCount = _pts.Count;
            _lr.SetPositions(_pts.ToArray());
        }
    }
}