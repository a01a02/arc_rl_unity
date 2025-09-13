/* ActionTrajectoryPreview
 * Visual-only bicycle model rollout from the car pose using the last received
 * (steer, throttle). Renders above the floor using an overlay shader if present.
 * Toggle with 'T'.
 * This does NOT affect control; it is a passive preview for debugging.*/

using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.Rendering;

[RequireComponent(typeof(LineRenderer))]
public class ActionTrajectoryPreview : MonoBehaviour
{
    [Header("Refs")]
    public RLClientSender sender;
    public SimpleCarController controller;
    public Rigidbody rb;

    [Header("Bicycle Kinematics")]
    public float wheelBase = 2.4f;
    public float maxSteerDeg = 30f;
    public float accelPerThrottle = 4.0f;
    public float drag = 0.4f;

    [Header("Rollout")]
    public float minHorizonSec = 0.8f;
    public float maxHorizonSec = 1.6f;
    public float vRefForMaxHorizon = 8f;
    public float dt = 0.04f;
    public float yOffset = 0.2f;

    [Header("Preview Stabilization")]
    [Range(0f, 1f)] public float inputSmoothing = 0.5f;
    public float maxSteerRateDegPerSec = 180f;
    public bool invertSteerSign = false;

    [Header("Style")]
    public float lineWidth = 0.08f;
    public Color lineColor = new Color(0.12f, 0.9f, 0.9f, 1f);
    public KeyCode toggleKey = KeyCode.T;

    [Header("Material (optional)")]
    public Material alwaysOnTopMaterial; // Uses Hidden/AlwaysOnTopLine or URP variant if null

    private LineRenderer _lr;
    private bool _visible = true;
    private float _steerLP = 0f, _thrLP = 0f;
    private float _prevSteerDegForSlew = 0f;

    void Awake()
    {
        if (controller == null) controller = FindObjectOfType<SimpleCarController>();
        if (rb == null && controller != null) rb = controller.GetComponent<Rigidbody>();

        _lr = GetComponent<LineRenderer>();
        _lr.useWorldSpace = true;
        _lr.widthMultiplier = lineWidth;
        _lr.numCapVertices = 8;
        _lr.numCornerVertices = 8;
        _lr.alignment = LineAlignment.View;
        _lr.generateLightingData = false;
        _lr.shadowCastingMode = ShadowCastingMode.Off;
        _lr.receiveShadows = false;

        if (alwaysOnTopMaterial == null)
        {
            var sh = Shader.Find("Hidden/AlwaysOnTopLine");
            if (sh == null) sh = Shader.Find("Hidden/URP/AlwaysOnTopLine");
            if (sh != null) alwaysOnTopMaterial = new Material(sh);
        }
        if (alwaysOnTopMaterial != null)
        {
            _lr.material = alwaysOnTopMaterial;
            _lr.material.renderQueue = 5000;
        }
        else
        {
            _lr.material = new Material(Shader.Find("Sprites/Default"));
            _lr.material.renderQueue = 5000;
        }
        _lr.startColor = _lr.endColor = lineColor;
        _lr.positionCount = 0;
        _lr.sortingOrder = short.MaxValue;
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) _visible = !_visible;
    }

    void LateUpdate()
    {
        if (!_visible || controller == null || rb == null)
        {
            if (_lr != null) _lr.positionCount = 0;
            return;
        }

        Vector3 pos = controller.transform.position;
        pos.y += yOffset;

        Vector3 fwd = controller.transform.forward; // Unity +Z
        float yaw = Mathf.Atan2(fwd.x, fwd.z);
        float v = rb.velocity.magnitude;

        float steer = (sender != null) ? sender.lastSteerCmd : ReadFloat(controller, "steerInput", "steer", "Steer");
        float thr = (sender != null) ? sender.lastThrottleCmd : ReadFloat(controller, "throttleInput", "throttle", "Throttle");
        steer = Mathf.Clamp(steer, -1f, 1f);
        thr = Mathf.Clamp01(thr);

        float alpha = Mathf.Clamp01(1f - Mathf.Pow(1f - inputSmoothing, Mathf.Max(1f, Time.deltaTime / 0.02f)));
        _steerLP = Mathf.Lerp(_steerLP, steer, alpha);
        _thrLP = Mathf.Lerp(_thrLP, thr, alpha);

        float targetSteerDeg = (invertSteerSign ? -1f : 1f) * _steerLP * maxSteerDeg;
        float usedSteerDeg = targetSteerDeg;
        if (maxSteerRateDegPerSec > 0f)
        {
            float maxDelta = maxSteerRateDegPerSec * Mathf.Max(Time.deltaTime, 1f / 90f);
            usedSteerDeg = Mathf.MoveTowards(_prevSteerDegForSlew, targetSteerDeg, maxDelta);
        }
        _prevSteerDegForSlew = usedSteerDeg;
        float steerRad = usedSteerDeg * Mathf.Deg2Rad;

        float tH = Mathf.Lerp(minHorizonSec, maxHorizonSec, Mathf.Clamp01(v / Mathf.Max(0.01f, vRefForMaxHorizon)));
        int steps = Mathf.Max(2, Mathf.RoundToInt(tH / Mathf.Max(0.01f, dt)));

        List<Vector3> pts = new List<Vector3>(steps + 1);
        pts.Add(pos);

        for (int i = 0; i < steps; i++)
        {
            float a = accelPerThrottle * _thrLP - drag * v;
            v = Mathf.Max(0f, v + a * dt);

            float omega = (Mathf.Abs(steerRad) < 1e-4f) ? 0f : (v / wheelBase) * Mathf.Tan(steerRad);
            yaw += omega * dt;

            Vector3 fwdW = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            pos += fwdW * v * dt;
            pos.y = controller.transform.position.y + yOffset;
            pts.Add(pos);
        }

        _lr.positionCount = pts.Count;
        _lr.SetPositions(pts.ToArray());
        _lr.startColor = _lr.endColor = lineColor;
    }

    float ReadFloat(object obj, params string[] names)
    {
        if (obj == null) return 0f;
        var t = obj.GetType();
        foreach (var n in names)
        {
            var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float)) return (float)f.GetValue(obj);
            var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanRead && p.PropertyType == typeof(float)) return (float)p.GetValue(obj, null);
        }
        return 0f;
    }
}