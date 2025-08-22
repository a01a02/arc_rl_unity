using UnityEngine;

[RequireComponent(typeof(Collider))]
public class KillZone : MonoBehaviour
{
    void Awake() { var col = GetComponent<Collider>(); col.isTrigger = true; }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponentInParent<SimpleCarController>() != null)
            SharedEpisodeFlags.TriggerKill();
    }
}