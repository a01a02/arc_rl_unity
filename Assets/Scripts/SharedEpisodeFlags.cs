/* SharedEpisodeFlags
 * Optional shared container for episode state so multiple components can read/write
 * without tight coupling. RLClientSender can write into it; HUDs can read from it.
 * If you already use RLClientSender's public getters, you can skip this script. */

using UnityEngine;

public class SharedEpisodeFlags : MonoBehaviour
{
    [Header("Episode State (read/write)")]
    public int stepCount = 0;
    public float lastReward = 0f;
    public bool done = false;
    public bool truncated = false;

    [Header("Misc Telemetry (optional)")]
    public float speedMps = 0f;
    public float steerCmd = 0f;
    public float throttleCmd = 0f;

    public void ResetFlags()
    {
        stepCount = 0;
        lastReward = 0f;
        done = false;
        truncated = false;
        speedMps = 0f;
        steerCmd = 0f;
        throttleCmd = 0f;
    }
}