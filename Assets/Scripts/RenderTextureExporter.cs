using UnityEngine;
using System.IO;
using System.Globalization;

public class RenderTextureExporter : MonoBehaviour
{
    public Camera renderCamera;
    public RenderTexture renderTexture;
    public string filePrefix = "CameraRGB";
    public string outputFolder = "Assets/Captures";
    public bool exportDepth = false;

    private Texture2D tex;
    private string poseLogPath;

    void Start()
    {
        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        tex = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);

        poseLogPath = Path.Combine(outputFolder, "camera_pose_log.csv");
        if (!File.Exists(poseLogPath))
        {
            File.WriteAllText(poseLogPath, "filename,x,y,z,qx,qy,qz,qw\n");
        }
    }

    public void CaptureFrame(int frameIndex)
    {
        // Ensure camera renders to target texture
        renderCamera.targetTexture = renderTexture;
        renderCamera.Render();

        // Activate and read pixels
        RenderTexture.active = renderTexture;
        tex.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
        tex.Apply();

        // Timestamped filename
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
        string fileName = $"{filePrefix}_{timestamp}_{frameIndex:D4}.jpg";
        string fullPath = Path.Combine(outputFolder, fileName);

        // Save JPG
        File.WriteAllBytes(fullPath, tex.EncodeToJPG());

        // Log pose
        Vector3 pos = renderCamera.transform.position;
        Quaternion rot = renderCamera.transform.rotation;

        string poseEntry = string.Format(CultureInfo.InvariantCulture,
            "{0},{1:F6},{2:F6},{3:F6},{4:F6},{5:F6},{6:F6},{7:F6}\n",
            fileName, pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w);

        File.AppendAllText(poseLogPath, poseEntry);

        Debug.Log($"{filePrefix} saved: {fileName}");

        // Cleanup
        RenderTexture.active = null;
    }

    void OnGUI()
    {
        if (tex != null)
        {
            GUI.DrawTexture(new Rect(10, 10, 256, 144), tex, ScaleMode.ScaleToFit, false);
        }
    }
}