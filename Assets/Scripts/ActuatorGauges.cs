/* ActuatorGauges
 * Simple immediate-mode GUI gauges for steer/throttle.
 * Toggle with 'G'. */

using UnityEngine;

public class ActuatorGauges : MonoBehaviour
{
    public RLClientSender sender; // drag ClientSender
    public KeyCode toggleKey = KeyCode.G;

    public Rect steerRect = new Rect(10, 120, 140, 140);
    public Rect thrRect = new Rect(160, 120, 34, 140);
    private bool _visible = true;

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) _visible = !_visible;
    }

    void OnGUI()
    {
        if (!_visible || sender == null) return;

        float s = Mathf.Clamp(sender.lastSteerCmd, -1f, 1f);
        float t = Mathf.Clamp01(sender.lastThrottleCmd);

        GUI.Box(steerRect, "Steer");
        var c = new Vector2(steerRect.x + steerRect.width / 2f, steerRect.y + steerRect.height / 2f);
        float r = Mathf.Min(steerRect.width, steerRect.height) * 0.35f;
        DrawCircle(c, r, 48);
        float ang = Mathf.Lerp(-45f, 45f, (s + 1f) / 2f) * Mathf.Deg2Rad;
        var p = c + new Vector2(Mathf.Cos(ang), -Mathf.Sin(ang)) * r;
        DrawLine(c, p, Color.yellow, 2f);

        GUI.Box(thrRect, "T");
        float filled = (thrRect.height - 20f) * t;
        var fill = new Rect(thrRect.x + 5f, thrRect.y + (thrRect.height - 10f - filled), thrRect.width - 10f, filled);
        DrawRect(fill, new Color(0.2f, 0.9f, 0.2f, 0.9f));
    }

    // Immediate-mode helpers
    static Texture2D _tex;
    static void EnsureTex()
    {
        if (_tex == null) { _tex = new Texture2D(1, 1); _tex.SetPixel(0, 0, Color.white); _tex.Apply(); }
    }

    void DrawLine(Vector2 a, Vector2 b, Color col, float width = 1f)
    {
        EnsureTex();
        var dif = b - a;
        float ang = Mathf.Atan2(dif.y, dif.x) * Mathf.Rad2Deg;
        float len = Mathf.Max(1f, dif.magnitude);
        var r = new Rect(a.x, a.y, len, width);
        Matrix4x4 m = GUI.matrix;
        GUI.color = col;
        GUIUtility.RotateAroundPivot(ang, a);
        GUI.DrawTexture(r, _tex);
        GUI.matrix = m;
        GUI.color = Color.white;
    }

    void DrawRect(Rect r, Color col)
    {
        EnsureTex();
        var c = GUI.color; GUI.color = col; GUI.DrawTexture(r, _tex); GUI.color = c;
    }

    void DrawCircle(Vector2 c, float r, int seg)
    {
        Vector2 prev = c + new Vector2(r, 0);
        for (int i = 1; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            Vector2 cur = c + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            DrawLine(prev, cur, Color.gray, 1f);
            prev = cur;
        }
    }
}