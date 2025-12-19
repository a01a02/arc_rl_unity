using UnityEngine;

[DisallowMultipleComponent]
public class TelemetryHUD : MonoBehaviour
{
    [SerializeField] private RLClientSender sender;
    [SerializeField] private GoalProximity goalProximity;
    [SerializeField] private bool show = true;

    void Awake()
    {
        if (sender == null) sender = FindObjectOfType<RLClientSender>();
        if (goalProximity == null) goalProximity = FindObjectOfType<GoalProximity>();
    }

    void OnGUI()
    {
        if (!show || sender == null) return;

        var style = new GUIStyle(GUI.skin.label)
        {
            normal = { textColor = Color.black },
            fontSize = 14,
            alignment = TextAnchor.UpperRight
        };

        const int w = 360, h = 48;
        int x = Screen.width - w - 8, y = 8;
        GUI.Box(new Rect(x, y, w, h), GUIContent.none);

        if (goalProximity != null && sender.CarBody != null)
        {
            goalProximity.GetDistances(out float dxz, out float dy);
            bool inside = goalProximity.IsInside(sender.CarBody.position);

            GUI.Label(new Rect(x + 8, y + 6, w - 16, 20),
                $"Goal dXZ={dxz:0.0}m dy={dy:+0.0;-0.0} inside={inside}", style);
            GUI.Label(new Rect(x + 8, y + 24, w - 16, 20),
                $"Cmd steer={sender.LastSteer:+0.00;-0.00} thr={sender.LastThrottle:0.00}", style);
        }
        else
        {
            GUI.Label(new Rect(x + 8, y + 6, w - 16, 20), "Goal: n/a", style);
        }
    }
}