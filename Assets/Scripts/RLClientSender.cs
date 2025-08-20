using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// RLClientSender
/// 
/// Unity acts as a TCP server for the Python training client.
/// Protocol:
///   RESET: Python sends one byte 'R' (0x52). Unity immediately returns len|jpeg|reward|done|truncated.
///   STEP : Python sends two big-endian float32: (steer, throttle).
///          Unity applies action for one FixedUpdate, captures a JPEG, computes reward/done/truncated,
///          and returns len|jpeg|reward|done|truncated.
/// 
/// Notes:
/// - RGB only. No masks/flows/heuristics.
/// - Use the same crop fraction/resolution on the Python side (recommended). Unity sends full frames.
/// - Capture uses ReadPixels for compatibility; you can switch to AsyncGPUReadback for perf if needed.
/// </summary>
[DefaultExecutionOrder(-20)]
public class RLClientSender : MonoBehaviour
{
    [Header("Network")]
    public int port = 5556;

    [Header("Capture")]
    public Camera captureCamera;
    public int outputWidth = 84;
    public int outputHeight = 84;
    [Range(1,100)] public int jpegQuality = 80;

    [Header("Episode")]
    public int maxSteps = 500;
    public SimpleCarController controller;
    public Rigidbody rb;

    // Internal
    private TcpListener _listener;
    private TcpClient _client;
    private NetworkStream _stream;
    private Thread _netThread;
    private volatile bool _running = false;

    private volatile bool _resetRequested = false;
    private volatile bool _stepRequested = false;
    private volatile float _reqSteer = 0f;
    private volatile float _reqThrottle = 0f;

    private int _stepCount = 0;
    private bool _done = false;
    private bool _truncated = false;
    private float _prevSteer = 0f;

    private Texture2D _readbackTex; // reused

    void Start()
    {
        Application.runInBackground = true;
        if (captureCamera == null) throw new Exception("RLClientSender: captureCamera not set");
        if (controller == null) throw new Exception("RLClientSender: controller not set");
        if (rb == null) rb = controller.GetComponent<Rigidbody>();
        if (rb == null) throw new Exception("RLClientSender: no Rigidbody found");

        _readbackTex = new Texture2D(outputWidth, outputHeight, TextureFormat.RGB24, false);

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _netThread = new Thread(NetLoop) { IsBackground = true };
        _netThread.Start();

        StartCoroutine(MainLoop());
        Debug.Log($"RLClientSender: listening on 0.0.0.0:{port}");
    }

    void OnDestroy()
    {
        _running = false;
        try { _listener?.Stop(); } catch {}
        try { _stream?.Close(); } catch {}
        try { _client?.Close(); } catch {}
        try { if (_netThread != null && _netThread.IsAlive) _netThread.Join(200); } catch {}
        if (_readbackTex != null) Destroy(_readbackTex);
    }

    private void NetLoop()
    {
        while (_running)
        {
            try
            {
                if (_client == null)
                {
                    _client = _listener.AcceptTcpClient(); // blocking
                    _stream = _client.GetStream();
                    _client.NoDelay = true;
                    Debug.Log("RLClientSender: client connected");
                }

                // Read one byte to distinguish RESET vs ACTION
                int b = _stream.ReadByte();
                if (b < 0)
                {
                    // disconnected
                    CleanupClient();
                    continue;
                }

                if (b == (byte)'R')
                {
                    _resetRequested = true;
                }
                else
                {
                    // This byte is the first byte of the 8-byte action payload (two float32 BE)
                    byte[] buf = new byte[8];
                    buf[0] = (byte)b;
                    int need = 7, got = 0;
                    while (got < need)
                    {
                        int n = _stream.Read(buf, 1 + got, need - got);
                        if (n <= 0) { CleanupClient(); goto CONTINUE; }
                        got += n;
                    }
                    // Parse BE floats
                    _reqSteer = ToSingleBE(buf, 0);
                    _reqThrottle = ToSingleBE(buf, 4);
                    _stepRequested = true;
                }

                CONTINUE: ;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"RLClientSender NetLoop exception: {e.Message}");
                CleanupClient();
            }
        }
    }

    private System.Collections.IEnumerator MainLoop()
    {
        // Wait a frame to let everything settle
        yield return null;

        while (_running)
        {
            if (_client == null || _stream == null || !_client.Connected)
            {
                yield return null;
                continue;
            }

            if (_resetRequested)
            {
                ResetEpisode();
                // Send initial observation (reward=0, done=false, truncated=false)
                yield return StartCoroutine(CaptureAndSend(0f, false, false));
                _resetRequested = false;
                continue;
            }

            if (_stepRequested && !_done && !_truncated)
            {
                // Apply action in FixedUpdate and wait one physics step
                controller.SetInputs(_reqSteer, _reqThrottle);
                yield return new WaitForFixedUpdate();
                // Optional: one more EndOfFrame to ensure render completion
                yield return new WaitForEndOfFrame();

                // Compute reward/done/truncated
                float fwdSpeed = Vector3.Dot(rb.velocity, rb.transform.forward);
                float deltaSteer = Mathf.Abs(controller.CurrentSteerNorm - _prevSteer);
                _prevSteer = controller.CurrentSteerNorm;

                float reward = 0.01f + 0.02f * Mathf.Max(0f, fwdSpeed) - 0.01f * deltaSteer;
                if (controller.ConsumeCollisionFlag())
                {
                    reward -= 1.0f;
                    _done = true;
                }

                _stepCount++;
                if (_stepCount >= maxSteps) _truncated = true;

                yield return StartCoroutine(CaptureAndSend(reward, _done, _truncated));

                _stepRequested = false;
                continue;
            }

            yield return null;
        }
    }

    private void ResetEpisode()
    {
        _stepCount = 0;
        _done = false;
        _truncated = false;
        _prevSteer = controller.CurrentSteerNorm;
        controller.ResetVehicle();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    private System.Collections.IEnumerator CaptureAndSend(float reward, bool done, bool truncated)
    {
        // Ensure we capture after rendering
        yield return new WaitForEndOfFrame();

        // Render the camera to a temporary RT at output size
        RenderTexture prev = RenderTexture.active;
        RenderTexture temp = RenderTexture.GetTemporary(outputWidth, outputHeight, 0, RenderTextureFormat.ARGB32);
        var prevTarget = captureCamera.targetTexture;
        captureCamera.targetTexture = temp;
        captureCamera.Render();
        RenderTexture.active = temp;

        _readbackTex.ReadPixels(new Rect(0,0,outputWidth,outputHeight), 0, 0, false);
        _readbackTex.Apply(false, false);

        // restore
        RenderTexture.active = prev;
        captureCamera.targetTexture = prevTarget;
        RenderTexture.ReleaseTemporary(temp);

        byte[] jpg = _readbackTex.EncodeToJPG(jpegQuality);

        try
        {
            // length prefix BE
            byte[] len = ToBE((uint)jpg.Length);
            _stream.Write(len, 0, 4);
            _stream.Write(jpg, 0, jpg.Length);

            // tail: reward (float32 BE), done (byte), truncated (byte)
            byte[] r = ToBE(reward);
            _stream.Write(r, 0, 4);
            _stream.WriteByte(done ? (byte)1 : (byte)0);
            _stream.WriteByte(truncated ? (byte)1 : (byte)0);
            _stream.Flush();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"RLClientSender send exception: {e.Message}");
            CleanupClient();
        }
    }

    private void CleanupClient()
    {
        try { _stream?.Close(); } catch {}
        try { _client?.Close(); } catch {}
        _stream = null;
        _client = null;
        _resetRequested = false;
        _stepRequested = false;
    }

    // ---- Helpers ----
    private static byte[] ToBE(uint v)
    {
        byte[] b = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return b;
    }
    private static byte[] ToBE(float v)
    {
        byte[] b = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return b;
    }
    private static float ToSingleBE(byte[] buf, int offset)
    {
        byte[] t = new byte[4];
        Buffer.BlockCopy(buf, offset, t, 0, 4);
        if (BitConverter.IsLittleEndian) Array.Reverse(t);
        return BitConverter.ToSingle(t, 0);
    }
}