using UnityEngine;

public class RoutePath : MonoBehaviour
{
    [Tooltip("Collect child transforms as waypoints at Start if true")]
    public bool autoCollectChildren = true;

    public Transform[] waypoints;

    void Awake()
    {
        if (autoCollectChildren)
        {
            int n = transform.childCount;
            waypoints = new Transform[n];
            for (int i = 0; i < n; i++) waypoints[i] = transform.GetChild(i);
        }
    }
}