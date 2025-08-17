using UnityEngine;

public class FrameCaptureManager : MonoBehaviour
{
    public RenderTextureExporter[] exporters;
    public int captureEveryNFrames = 1;

    private int frameIndex = 0;

    void Update()
    {
        if (Time.frameCount % captureEveryNFrames == 0)
        {
            foreach (var exporter in exporters)
            {
                exporter.CaptureFrame(frameIndex);
            }

            frameIndex++;
        }
    }
}