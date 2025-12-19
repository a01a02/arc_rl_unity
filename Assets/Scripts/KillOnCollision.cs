using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class KillOnCollision : MonoBehaviour
{
    public RLClientSender sender;
    [Tooltip("Layers treated as lethal on contact (e.g., Buildings/Obstacles).")]
    public LayerMask lethalLayers;

    void OnCollisionEnter(Collision c)
    {
        if (sender == null) return;
        int l = c.collider.gameObject.layer;
        if (((1 << l) & lethalLayers.value) != 0)
            sender.ExternalKill();
    }
}