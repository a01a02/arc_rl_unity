/* TriggerProbe
 * Utility component to debug trigger volumes. Counts contacts from objects
 * with a given tag and (optionally) tints an assigned Renderer while inside.
 * Purely for debugging; safe to remove in production. */

using UnityEngine;

[RequireComponent(typeof(Collider))]
public class TriggerProbe : MonoBehaviour
{
    public string matchTag = "Player";
    public Renderer tintRenderer;
    public Color insideColor = new Color(1f, 0.3f, 0.3f, 1f);
    public Color outsideColor = Color.white;

    [Header("Debug")]
    public int contactCount = 0;

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!string.IsNullOrEmpty(matchTag) && !other.CompareTag(matchTag)) return;
        contactCount++;
        UpdateTint();
    }

    void OnTriggerExit(Collider other)
    {
        if (!string.IsNullOrEmpty(matchTag) && !other.CompareTag(matchTag)) return;
        contactCount = Mathf.Max(0, contactCount - 1);
        UpdateTint();
    }

    void UpdateTint()
    {
        if (tintRenderer == null) return;
        var mats = tintRenderer.materials;
        for (int i = 0; i < mats.Length; i++)
        {
            mats[i].color = (contactCount > 0) ? insideColor : outsideColor;
        }
    }
}