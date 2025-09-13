/* GoalTrigger
 * Place on a trigger collider around the final goal area.
 * When the car enters, raises OnGoalReached (once per episode).*/

using System;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class GoalTrigger : MonoBehaviour
{
    public event Action OnGoalReached;

    [Tooltip("Tag used by the agent/car root GameObject (optional).")]
    public string carTag = "Player";

    private bool _hit = false;
    
    void Reset()
    {
        var col = GetComponent<Collider>();
        col.isTrigger = true;
    }

    public void ResetForNewEpisode()
    {
        _hit = false;
    }

    void OnTriggerEnter(Collider other)
    {
        if (_hit) return;
        if (!string.IsNullOrEmpty(carTag) && !other.CompareTag(carTag)) return;

        _hit = true;
        OnGoalReached?.Invoke();
    }
}