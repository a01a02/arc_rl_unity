/* OffRoadTrigger
 * Put this on trigger volumes marking areas off the drivable road (e.g., medians/shoulders).
 * While the car stays inside, it emits a contact event each FixedUpdate. The sender can
 * convert this into a time-based penalty.*/

using System;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class OffRoadTrigger : MonoBehaviour
{
    public event Action OnOffRoadContact;

    [Tooltip("Tag used by the agent/car root GameObject (optional).")]
    public string carTag = "Player";

    private int _contacts = 0;

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    public void ResetForNewEpisode()
    {
        _contacts = 0;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!string.IsNullOrEmpty(carTag) && !other.CompareTag(carTag)) return;
        _contacts++;
    }

    void OnTriggerExit(Collider other)
    {
        if (!string.IsNullOrEmpty(carTag) && !other.CompareTag(carTag)) return;
        _contacts = Mathf.Max(0, _contacts - 1);
    }

    void FixedUpdate()
    {
        if (_contacts > 0)
            OnOffRoadContact?.Invoke();
    }
}