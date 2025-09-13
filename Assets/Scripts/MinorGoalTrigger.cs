/* MinorGoalTrigger
 * Place on optional sub-goal triggers (e.g., waypoints). Each time the
 * car enters, raises OnMinorGoal once per episode (per trigger).*/

using System;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class MinorGoalTrigger : MonoBehaviour
{
    public event Action OnMinorGoal;

    [Tooltip("Tag used by the agent/car root GameObject (optional).")]
    public string carTag = "Player";

    private bool _hit = false;

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
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
        OnMinorGoal?.Invoke();
    }
}