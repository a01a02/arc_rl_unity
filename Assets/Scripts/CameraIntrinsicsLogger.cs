using UnityEngine;
using System.IO;

public class CameraIntrinsicsLogger : MonoBehaviour
{
    public Camera targetCamera;
    public RenderTexture renderTexture;
    public string outputPath = "Assets/Captures/camera_intrinsics.yaml";

    void Start()
    {
        LogIntrinsics();
    }

    void LogIntrinsics()
    {
        if (targetCamera == null || renderTexture == null)
        {
            Debug.LogError("Camera or RenderTexture not set.");
            return;
        }

        float fov = targetCamera.fieldOfView;
        int width = renderTexture.width;
        int height = renderTexture.height;

        float fx = (width / 2f) / Mathf.Tan(0.5f * fov * Mathf.Deg2Rad);
        float fy = fx;
        float cx = width / 2f;
        float cy = height / 2f;

        string yaml = 
            $"image_width: {width}\n" +
            $"image_height: {height}\n" +
            $"camera_matrix:\n" +
            $"  fx: {fx}\n" +
            $"  fy: {fy}\n" +
            $"  cx: {cx}\n" +
            $"  cy: {cy}\n" +
            $"field_of_view: {fov}\n" +
            $"camera_name: {targetCamera.name}\n";

        File.WriteAllText(outputPath, yaml);
        Debug.Log($"Camera intrinsics saved to {outputPath}");
    }
}