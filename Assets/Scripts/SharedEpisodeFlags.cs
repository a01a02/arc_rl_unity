using UnityEngine;

public static class SharedEpisodeFlags
{
    public static bool GoalReached { get; private set; }
    public static float GoalBonus { get; private set; }

    // Minor goals
    public static int MinorGoalsReached { get; private set; }

    // Off-road & kill
    public static bool OffRoadContact { get; private set; } // true while inside an off-road trigger
    public static bool KillNow { get; private set; } // end episode immediately

    public static void SetGoal(float bonus) { GoalReached = true; GoalBonus = bonus; }
    public static void AddMinorGoal() { MinorGoalsReached++; }
    public static void SetOffRoad(bool v) { OffRoadContact = v; }
    public static void TriggerKill() { KillNow = true; }

    public static void ResetFlags()
    {
        GoalReached = false; GoalBonus = 0f;
        MinorGoalsReached = 0;
        OffRoadContact = false;
        KillNow = false;
    }
}