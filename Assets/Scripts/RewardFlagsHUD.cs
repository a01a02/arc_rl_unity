// Assets/Scripts/RewardFlagsHUD.cs
using UnityEngine;
public class RewardFlagsHUD : MonoBehaviour
{
    public RLClientSender sender;
    void OnGUI()
    {
        if (!sender) return;
        GUILayout.BeginArea(new Rect(10,10,360,120), GUI.skin.box);
        GUILayout.Label($"GoalReached={SharedEpisodeFlags.GoalReached}  MinorGoals={SharedEpisodeFlags.MinorGoalsReached}");
        GUILayout.Label($"OffRoad={SharedEpisodeFlags.OffRoadContact}  KillNow={SharedEpisodeFlags.KillNow}");
        GUILayout.Label($"MaxSteps={sender.maxSteps}  JPEG={sender.jpegQuality}");
        GUILayout.EndArea();
    }
}