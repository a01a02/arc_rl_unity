using UnityEngine;

[RequireComponent(typeof(Collider))]
public class GoalTrigger : MonoBehaviour
{
    [Tooltip("Bonus added to reward when this goal is reached")]
    public float bonus = 2.0f;

    [Tooltip("If true, this trigger deactivates after one hit until the next episode reset")]
    public bool oneShot = true;

    [Tooltip("Treat objects with this tag as the player (leave empty to auto-detect by SimpleCarController)")]
    public string playerTag = "Player";

    private bool _consumed = false;
    private Collider _col;

    void Awake()
    {
        _col = GetComponent<Collider>();
        if (_col != null) _col.isTrigger = true;
    }

    /// <summary>Called by RLClientSender at the start of each episode.</summary>
    public void ResetForNewEpisode()
    {
        _consumed = false;
        if (_col != null) _col.enabled = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (_consumed && oneShot) return;

        // Identify the car: by tag or by component in hierarchy
        bool isPlayer = false;
        if (!string.IsNullOrEmpty(playerTag) && other.CompareTag(playerTag))
        {
            isPlayer = true;
        }
        else
        {
            if (other.GetComponentInParent<SimpleCarController>() != null) isPlayer = true;
        }

        if (!isPlayer) return;

        SharedEpisodeFlags.SetGoal(bonus);

        if (oneShot)
        {
            _consumed = true;
            if (_col != null) _col.enabled = false;
        }
    }
}