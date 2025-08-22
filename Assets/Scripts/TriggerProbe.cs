// Assets/Scripts/TriggerProbe.cs
using UnityEngine;
public class TriggerProbe : MonoBehaviour
{
    void OnTriggerEnter(Collider other)
    {
        Debug.Log($"[TriggerProbe] {name} hit by {other.name} (root={other.transform.root.name})");
    }
}