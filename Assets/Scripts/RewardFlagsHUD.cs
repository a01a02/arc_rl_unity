using UnityEngine;

[DisallowMultipleComponent]
public class RewardFlagsHUD : MonoBehaviour
{
    [SerializeField] private RLClientSender sender;
    [SerializeField] private bool show = true;

    void Awake()
    {
        if (sender == null) sender = FindObjectOfType<RLClientSender>();
    }

    void OnGUI()
    {
        if (!show || sender == null) return;

        var style = new GUIStyle(GUI.skin.label)
        {
            normal = { textColor = Color.black },
            fontSize = 14
        };

        const int w = 360, h = 48;
        int x = Screen.width - w - 8;
        int y = Screen.height - h - 58; // sits above RLClientSender bottom box
        GUI.Box(new Rect(x, y, w, h), GUIContent.none);

        GUI.Label(new Rect(x + 8, y + 6, w - 16, 20),
            $"Step={sender.StepCount}  R={sender.LastReward:+0.000;-0.000}", style);
        GUI.Label(new Rect(x + 8, y + 24, w - 16, 20),
            $"Done={sender.EpisodeDone}  Trunc={sender.EpisodeTruncated}", style);
    }
}