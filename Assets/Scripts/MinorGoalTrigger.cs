using UnityEngine;

[RequireComponent(typeof(Collider))]
public class MinorGoalTrigger : MonoBehaviour
{
    [Tooltip("Optional sequential order (0,1,2,...). Set -1 to ignore order.")]
    public int sequenceIndex = -1;
    public bool oneShot = true;
    public string playerTag = "Player";

    private bool _consumed = false;
    private Collider _col;

    void Awake()
    {
        _col = GetComponent<Collider>();
        _col.isTrigger = true;
    }

    public void ResetForNewEpisode()
    {
        _consumed = false;
        if (_col != null) _col.enabled = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (_consumed && oneShot) return;

        bool isPlayer = (!string.IsNullOrEmpty(playerTag) && other.CompareTag(playerTag)) ||
                        other.GetComponentInParent<SimpleCarController>() != null;
        if (!isPlayer) return;

        // (Optional) enforce order by sequenceIndex if you want — simplest: accept all
        SharedEpisodeFlags.AddMinorGoal();

        if (oneShot)
        {
            _consumed = true;
            if (_col != null) _col.enabled = false;
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        var c = GetComponent<Collider>();
        if (c is BoxCollider bc)
            Gizmos.DrawWireCube(bc.bounds.center, bc.bounds.size);
        else if (c is SphereCollider sc)
            Gizmos.DrawWireSphere(sc.bounds.center, sc.radius);
    }
#endif
}