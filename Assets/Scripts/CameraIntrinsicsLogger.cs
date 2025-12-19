/*
 * CameraIntrinsicsLogger
 * ----------------------
 * Logs camera intrinsics (fx, fy, cx, cy) for your capture resolution.
 *
 * Why this version?
 * - Lets you explicitly assign the capture camera (no auto-added Camera).
 * - Derives the logging resolution in this priority:
 *     1) RLClientSender.outputWidth/Height (if provided)
 *     2) targetCamera.targetTexture (if assigned)
 *     3) targetCamera.pixelWidth/Height
 * - Computes fx/fy from *vertical* FOV correctly:
 *     fy = (H/2) / tan(fov_y/2),  fx = fy * (W/H)
 * - Writes a simple YAML-like file to persistentDataPath.
 *
 * Usage:
 * - Put this script anywhere (does not need to be on the Camera).
 * - Assign `targetCamera` to your capture camera.
 * - (Optional) Assign `sender` so it logs the exact RL output size (e.g., 84x84).
 * - Press Play (if logOnStart=true) or use the context menu "Log Now".
 */

using System.IO;
using UnityEngine;

public class CameraIntrinsicsLogger : MonoBehaviour
{
    [Header("Targets")]
    [Tooltip("The camera used for RL capture (assign your Capture Camera here).")]
    public Camera targetCamera;

    [Tooltip("Optional: if assigned, will use sender.outputWidth/Height as logging resolution.")]
    public RLClientSender sender;

    [Tooltip("Optional: if set, overrides targetCamera.targetTexture when deriving size.")]
    public RenderTexture overrideRenderTexture;

    [Header("Output")]
    [Tooltip("File name (written under Application.persistentDataPath).")]
    public string outputFile = "CameraIntrinsics.yaml";

    [Tooltip("Automatically write on Start().")]
    public bool logOnStart = true;

    [Tooltip("Append a timestamp to the file name to avoid overwriting.")]
    public bool appendTimestamp = false;

    void Start()
    {
        if (logOnStart) LogNow();
    }

    [ContextMenu("Log Now")]
    public void LogNow()
    {
        if (targetCamera == null)
        {
            Debug.LogError("[CameraIntrinsicsLogger] targetCamera is not assigned.");
            return;
        }

        // Determine output size
        int W = 0, H = 0;

        if (sender != null && sender.outputWidth > 0 && sender.outputHeight > 0)
        {
            W = sender.outputWidth;
            H = sender.outputHeight;
        }
        else
        {
            var rt = overrideRenderTexture != null ? overrideRenderTexture : targetCamera.targetTexture;
            if (rt != null)
            {
                W = rt.width;
                H = rt.height;
            }
            else
            {
                // Fallback to on-screen camera size
                W = Mathf.Max(1, targetCamera.pixelWidth);
                H = Mathf.Max(1, targetCamera.pixelHeight);
            }
        }

        // Intrinsics from vertical FOV (Unity's Camera.fieldOfView is vertical FOV in degrees)
        float fovyRad = targetCamera.fieldOfView * Mathf.Deg2Rad; // vertical FOV
        float fy = (H * 0.5f) / Mathf.Tan(fovyRad * 0.5f);
        float fx = fy * (W / Mathf.Max(1e-6f, (float)H));         // aspect = W/H
        float cx = W * 0.5f;
        float cy = H * 0.5f;

        string txt =
$@"# Unity Camera Intrinsics
width: {W}
height: {H}
fx: {fx}
fy: {fy}
cx: {cx}
cy: {cy}
fov_y_deg: {targetCamera.fieldOfView}
near: {targetCamera.nearClipPlane}
far: {targetCamera.farClipPlane}
notes: computed from vertical FOV; fx = fy * (W/H)
";

        string fileName = outputFile;
        if (appendTimestamp)
        {
            string stem = Path.GetFileNameWithoutExtension(outputFile);
            string ext = Path.GetExtension(outputFile);
            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            fileName = $"{stem}_{stamp}{ext}";
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllText(path, txt);
        Debug.Log($"[CameraIntrinsicsLogger] Intrinsics saved to {path}");
    }
}