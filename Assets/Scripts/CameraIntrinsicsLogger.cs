using UnityEngine;
using System.IO;

/// <summary>
/// Logs both native and "effective" intrinsics after crop+resize,
/// to keep parity with the Python preprocessing.
/// 
/// If you crop in Python (recommended), set cropTopFrac and output size
/// to match the Python constants so you can compute effective intrinsics here for reference.
/// </summary>
public class CameraIntrinsicsLogger : MonoBehaviour
{
    public Camera cam;
    [Header("Training-time Preprocess (for effective intrinsics)")]
    [Range(0f, 0.9f)] public float cropTopFrac = 0.25f; // must match Python
    public int outWidth = 84;
    public int outHeight = 84;

    public string outputPath = "CameraIntrinsics.yaml";

    void Start()
    {
        if (cam == null) cam = GetComponent<Camera>();
        if (cam == null) { Debug.LogError("CameraIntrinsicsLogger: no Camera"); return; }

        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        float fx = cam.projectionMatrix[0,0] * 0.5f * w; // approximate; unity PM details vary
        float fy = cam.projectionMatrix[1,1] * 0.5f * h;
        float cx = w * 0.5f;
        float cy = h * 0.5f;

        // Effective intrinsics (after top crop + resize)
        int cropTop = Mathf.RoundToInt(h * cropTopFrac);
        int hEffSrc = h - cropTop;
        float sx = (float)outWidth / (float)w;
        float sy = (float)outHeight / (float)hEffSrc;

        float fxEff = fx * sx;
        float fyEff = fy * sy;
        float cxEff = cx * sx;                 // x-center unchanged (no horiz crop)
        float cyEff = (cy - cropTop) * sy;     // y shifts by cropTop then scales

        string yaml = ""
            + "native:\n"
            + $"  width: {w}\n"
            + $"  height: {h}\n"
            + $"  fx: {fx}\n"
            + $"  fy: {fy}\n"
            + $"  cx: {cx}\n"
            + $"  cy: {cy}\n"
            + "effective:\n"
            + $"  crop_top_frac: {cropTopFrac}\n"
            + $"  out_width: {outWidth}\n"
            + $"  out_height: {outHeight}\n"
            + $"  fx: {fxEff}\n"
            + $"  fy: {fyEff}\n"
            + $"  cx: {cxEff}\n"
            + $"  cy: {cyEff}\n";

        File.WriteAllText(outputPath, yaml);
        Debug.Log($"Camera intrinsics saved to {outputPath}");
    }
}