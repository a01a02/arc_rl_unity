/* RewardFlagsHUD
 * Tiny HUD to show current step, reward, and termination flags from RLClientSender.
 * Toggle visibility with 'H' to avoid clutter. */

using UnityEngine;

public class RewardFlagsHUD : MonoBehaviour
{
    public RLClientSender sender;
    public KeyCode toggleKey = KeyCode.H;

    public Rect area = new Rect(10, 36, 520, 70);
    private bool _visible = true;

    GUIStyle _label, _labelShadow, _box;

    void SetupStyles()
    {
        if (_label != null) return;
        _label = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        _labelShadow = new GUIStyle(_label);
        _labelShadow.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
        _box = new GUIStyle(GUI.skin.box);
        _box.normal.background = Texture2D.grayTexture;
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) _visible = !_visible;
    }

    void OnGUI()
    {
        if (!_visible || sender == null) return;
        SetupStyles();

        GUILayout.BeginArea(area, _box);

        DrawShadowed($"Step={sender.StepCount}  Reward={sender.LastReward:+0.000;-0.000}");
        DrawShadowed($"Done={sender.EpisodeDone}  Truncated={sender.EpisodeTruncated}  JPEG={sender.jpegQuality}  MaxSteps={sender.maxSteps}");

        GUILayout.EndArea();
    }

    void DrawShadowed(string text)
    {
        var r = GUILayoutUtility.GetRect(new GUIContent(text), _label);
        var s = r; s.x += 1; s.y += 1;
        GUI.Label(s, text, _labelShadow);
        GUI.Label(r, text, _label);
    }
}