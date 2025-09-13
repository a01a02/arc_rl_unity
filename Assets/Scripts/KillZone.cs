/* KillZone
 * Attach to trigger volumes representing fatal areas (fall off map, deep voids, etc.).
 * Signals a truncation/termination to the sender via OnKill.*/

using System;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class KillZone : MonoBehaviour
{
    public event Action OnKill;

    [Tooltip("Tag used by the agent/car root GameObject (optional).")]
    public string carTag = "Player";

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    public void ResetForNewEpisode() {/* stateless */}

    void OnTriggerEnter(Collider other)
    {
        if (!string.IsNullOrEmpty(carTag) && !other.CompareTag(carTag)) return;
        OnKill?.Invoke();
    }
}