/* CameraIntrinsicsLogger
 * Logs camera intrinsics to a YAML-like text file for reproducibility.
 * Run once on Start, or call LogNow() manually. */

using System.IO;
using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CameraIntrinsicsLogger : MonoBehaviour
{
    public string outputFile = "CameraIntrinsics.yaml";
    public bool logOnStart = true;

    void Start()
    {
        if (logOnStart) LogNow();
    }

    [ContextMenu("Log Now")]
    public void LogNow()
    {
        var cam = GetComponent<Camera>();
        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        float fx = (w / 2f) / Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad / 2f);
        float fy = fx; // assuming square pixels for typical Unity camera
        float cx = w / 2f;
        float cy = h / 2f;

        string txt = $@"# Unity Camera Intrinsics width: {w} height: {h} fx: {fx} fy: {fy} cx: {cx} cy: {cy} fov_deg: {cam.fieldOfView} near: {cam.nearClipPlane} far: {cam.farClipPlane}";

        var path = Path.Combine(Application.persistentDataPath, outputFile);
        File.WriteAllText(path, txt);
        Debug.Log($"Camera intrinsics saved to {path}");
    }
}