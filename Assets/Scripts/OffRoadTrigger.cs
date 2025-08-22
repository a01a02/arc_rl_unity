using UnityEngine;

[RequireComponent(typeof(Collider))]
public class OffRoadTrigger : MonoBehaviour
{
    void Awake() { var col = GetComponent<Collider>(); col.isTrigger = true; }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponentInParent<SimpleCarController>() != null)
            SharedEpisodeFlags.SetOffRoad(true);
    }

    void OnTriggerExit(Collider other)
    {
        if (other.GetComponentInParent<SimpleCarController>() != null)
            SharedEpisodeFlags.SetOffRoad(false);
    }

#if UNITY_EDITOR
    void OnDrawGizmos() { Gizmos.color = new Color(1f, 0.4f, 0.4f, 0.6f); }
#endif
}