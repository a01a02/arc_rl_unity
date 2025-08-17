using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class RLClientSender : MonoBehaviour
{
    public Camera captureCamera;
    public string serverHost = "127.0.0.1";  // Change to your server IP if needed
    public int serverPort = 5555;
    public SimpleCarController carController; // Assign in Inspector or auto-find

    private TcpClient client;
    private NetworkStream stream;
    private RenderTexture renderTexture;
    private Texture2D frameTexture;

    private bool connected = false;

    void Start()
    {
        if (carController == null)
        {
            carController = GetComponent<SimpleCarController>();
            if (carController == null)
                Debug.LogWarning("[RL CLIENT] No SimpleCarController found on GameObject.");
        }

        renderTexture = new RenderTexture(1920, 1080, 24);
        frameTexture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
        captureCamera.targetTexture = renderTexture;

        try
        {
            client = new TcpClient(serverHost, serverPort);
            stream = client.GetStream();
            connected = true;
            Debug.Log("[RL CLIENT] Connected to inference server");
        }
        catch (Exception e)
        {
            Debug.LogError("[RL CLIENT] Connection failed: " + e.Message);
        }

        InvokeRepeating(nameof(SendFrameAndReceiveAction), 0.1f, 0.1f); // ~10 FPS
    }

    void SendFrameAndReceiveAction()
    {
        if (!connected || stream == null) return;

        try
        {
            // Render frame
            RenderTexture.active = renderTexture;
            captureCamera.Render();
            frameTexture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            frameTexture.Apply();
            byte[] jpgBytes = frameTexture.EncodeToJPG();

            // Send length-prefixed image
            byte[] lengthPrefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(jpgBytes.Length));
            stream.Write(lengthPrefix, 0, lengthPrefix.Length);
            stream.Write(jpgBytes, 0, jpgBytes.Length);

            // Read 8 bytes for steering and throttle
            byte[] actionBytes = ReadExactBytes(8);
            if (actionBytes == null) throw new IOException("Failed to receive full action response");

            float steering = ToFloatFromBigEndian(actionBytes, 0);
            float throttle = ToFloatFromBigEndian(actionBytes, 4);

            Debug.Log($"[RL CLIENT] Steering: {steering:F3}, Throttle: {throttle:F3}");

            // Apply control to car
            carController?.SetInputs(steering, throttle);
        }
        catch (Exception e)
        {
            Debug.LogError("[RL CLIENT] Error: " + e.Message);
            connected = false;
            stream?.Close();
            client?.Close();
        }
    }

    byte[] ReadExactBytes(int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int bytesRead = stream.Read(buffer, offset, count - offset);
            if (bytesRead <= 0) return null;
            offset += bytesRead;
        }
        return buffer;
    }

    float ToFloatFromBigEndian(byte[] bytes, int start)
    {
        if (BitConverter.IsLittleEndian)
        {
            byte[] reversed = new byte[4];
            Array.Copy(bytes, start, reversed, 0, 4);
            Array.Reverse(reversed);
            return BitConverter.ToSingle(reversed, 0);
        }
        else
        {
            return BitConverter.ToSingle(bytes, start);
        }
    }

    void OnApplicationQuit()
    {
        if (connected)
        {
            stream?.Close();
            client?.Close();
        }
    }
}