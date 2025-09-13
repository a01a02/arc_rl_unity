/* TelemetryHUD
 * On-screen text HUD showing networking and vehicle telemetry.
 * Toggle with 'H'. Non-intrusive and purely visual. */

using UnityEngine;

public class TelemetryHUD : MonoBehaviour
{
    public RLClientSender sender;
    public SimpleCarController controller;
    public KeyCode toggleKey = KeyCode.H;

    [Header("Layout")]
    public Rect area = new Rect(10, 10, 420, 100);

    private bool _visible = true;
    private GUIStyle _label, _shadow, _box;

    void Awake()
    {
        if (controller == null) controller = FindObjectOfType<SimpleCarController>();
        if (sender == null) sender = FindObjectOfType<RLClientSender>();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) _visible = !_visible;
    }

    void Setup()
    {
        if (_label != null) return;
        _label = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        _shadow = new GUIStyle(_label);
        _shadow.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
        _box = new GUIStyle(GUI.skin.box);
        _box.normal.background = Texture2D.grayTexture;
    }

    void OnGUI()
    {
        if (!_visible) return;
        Setup();

        GUILayout.BeginArea(area, _box);

        float speed = (controller != null && controller.Body != null)
            ? controller.Body.velocity.magnitude
            : 0f;

        Draw($"Step={sender?.StepCount ?? 0}  Reward={sender?.LastReward:+0.000;-0.000}  Done={sender?.EpisodeDone}  Trunc={sender?.EpisodeTruncated}");
        Draw($"Steer={sender?.lastSteerCmd:+0.00;-0.00}  Throttle={sender?.lastThrottleCmd:0.00}  Speed={speed:0.00} m/s  JPEG={sender?.jpegQuality}");

        GUILayout.EndArea();
    }

    void Draw(string text)
    {
        var r = GUILayoutUtility.GetRect(new GUIContent(text), _label);
        var s = r; s.x += 1; s.y += 1;
        GUI.Label(s, text, _shadow);
        GUI.Label(r, text, _label);
    }
}