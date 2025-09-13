/*
 * RLClientSender
 * --------------
 * Unity-side TCP server that streams RGB frames + reward flags to a Python client
 * and receives (steer, throttle) actions each step.
 *
 * Protocol (matches live_unity_env.py):
 *  Reset:
 *    - Python → Unity: single byte 'R' (0x52)
 *    - Unity → Python: immediately sends  len(u32 BE) | jpeg(len) | reward(f32 BE) | done(u8) | truncated(u8)
 *  Step:
 *    - Python → Unity: steer(f32 BE) + throttle(f32 BE)
 *    - Unity → Python: len | jpeg | reward | done | truncated
 *
 * Observation: HWC uint8 RGB (e.g., 84x84x3), JPEG-encoded.
 * Action: steer ∈ [-1,1], throttle ∈ [0,1]
 *
 * Notes
 * - This component does NOT do any non-passive perception (no masks/flow).
 * - Rewards/terminations are determined by trigger components (Goal, MinorGoal,
 *   OffRoad, KillZone) and simple kinematics metrics (e.g., speed).
 * - Capturing occurs on the main thread (LateUpdate). Networking runs on a background
 *   thread reading actions and writing step payloads using thread-safe fields.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

[DisallowMultipleComponent]
public class RLClientSender : MonoBehaviour
{
    [Header("Network")]
    [Tooltip("TCP port to listen on (Python connects to this).")]
    public int port = 5556;

    [Header("Capture")]
    public Camera captureCamera;
    [Tooltip("Output image width in pixels.")]
    public int outputWidth = 84;
    [Tooltip("Output image height in pixels.")]
    public int outputHeight = 84;
    [Range(1, 100)]
    public int jpegQuality = 80;

    [Header("Control (read-only for other scripts)")]
    [Tooltip("Last steer command received from Python [-1..1].")]
    public float lastSteerCmd = 0f;
    [Tooltip("Last throttle command received from Python [0..1].")]
    public float lastThrottleCmd = 0f;

    [Header("Episode")]
    [Tooltip("Max physics steps before truncation (0 = unlimited).")]
    public int maxSteps = 500;

    [Header("References")]
    public Rigidbody carRb;
    public SimpleCarController carController;

    [Header("Reward Configuration")]
    [Tooltip("Small shaping reward each step while alive.")]
    public float aliveReward = 0.01f;
    [Tooltip("Scale for forward speed reward (m/s * scale).")]
    public float forwardSpeedScale = 0.02f;
    [Tooltip("Penalty per second when off road.")]
    public float offRoadPenaltyPerSec = 0.5f;

    [Header("Terminations / Flags (hook up triggers)")]
    public GoalTrigger finalGoal;
    public MinorGoalTrigger[] minorGoals;
    public OffRoadTrigger offRoad;
    public KillZone killZone;

    // internal state

    // Episode state (read/written only on main thread)
    private int _stepCount = 0;
    private bool _episodeDone = false;
    private bool _episodeTruncated = false;
    private float _lastReward = 0f;

    // Off-road contact timer
    private float _offRoadTimeAccum = 0f;

    // Networking
    private TcpListener _listener;
    private Thread _netThread;
    private volatile bool _shutdown = false;
    private volatile bool _clientConnected = false;

    // Shared payload fields (updated on main thread, read on net thread)
    private readonly object _jpegLock = new object();
    private byte[] _jpegBytes = Array.Empty<byte>();
    private int _jpegLen = 0;
    private volatile int _jpegStepIndex = -1; // step index that produced the jpeg

    // Action mailbox (written by net thread, read in FixedUpdate)
    private volatile float _steerMailbox = 0f;
    private volatile float _throttleMailbox = 0f;
    private volatile bool _resetRequested = false;

    // Capture resources
    private RenderTexture _rt;
    private Texture2D _readTex;

    // Public accessors for HUDs
    public int StepCount => _stepCount;
    public float LastReward => _lastReward;
    public bool EpisodeDone => _episodeDone;
    public bool EpisodeTruncated => _episodeTruncated;

    void Awake()
    {
        if (captureCamera == null)
            Debug.LogError("[RLClientSender] Capture Camera not assigned.");

        if (carRb == null && carController != null)
            carRb = carController.GetComponent<Rigidbody>();

        SetupRenderTargets();
    }

    void OnEnable()
    {
        _shutdown = false;
        StartNetworkThread();
        SubscribeTriggers(true);
    }

    void OnDisable()
    {
        _shutdown = true;
        StopNetworkThread();
        SubscribeTriggers(false);
        TeardownRenderTargets();
    }

    void OnApplicationQuit()
    {
        _shutdown = true;
        StopNetworkThread();
    }

    private void SetupRenderTargets()
    {
        _rt = new RenderTexture(outputWidth, outputHeight, 16, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 1,
            useMipMap = false
        };
        _rt.Create();

        _readTex = new Texture2D(outputWidth, outputHeight, TextureFormat.RGB24, false);
        _readTex.Apply(false, false);

        if (captureCamera != null)
            captureCamera.targetTexture = _rt;
    }

    private void TeardownRenderTargets()
    {
        if (captureCamera != null)
            captureCamera.targetTexture = null;

        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }

        if (_readTex != null)
        {
            Destroy(_readTex);
            _readTex = null;
        }
    }

    // Trigger wiring

    private void SubscribeTriggers(bool on)
    {
        if (finalGoal != null)
        {
            if (on) finalGoal.OnGoalReached += HandleFinalGoal;
            else finalGoal.OnGoalReached -= HandleFinalGoal;
        }
        if (minorGoals != null)
        {
            foreach (var mg in minorGoals)
            {
                if (mg == null) continue;
                if (on) mg.OnMinorGoal += HandleMinorGoal;
                else mg.OnMinorGoal -= HandleMinorGoal;
            }
        }
        if (offRoad != null)
        {
            if (on) offRoad.OnOffRoadContact += HandleOffRoad;
            else offRoad.OnOffRoadContact -= HandleOffRoad;
        }
        if (killZone != null)
        {
            if (on) killZone.OnKill += HandleKill;
            else killZone.OnKill -= HandleKill;
        }
    }

    private void HandleFinalGoal()
    {
        if (!_episodeDone && !_episodeTruncated)
        {
            _lastReward += 1.0f; // terminal reward
            _episodeDone = true;
        }
    }

    private void HandleMinorGoal()
    {
        if (!_episodeDone && !_episodeTruncated)
            _lastReward += 0.2f; // shaping for minor goals
    }

    private void HandleOffRoad()
    {
        // Accumulate time-based penalty in FixedUpdate via _offRoadTimeAccum
        _offRoadTimeAccum += Time.fixedDeltaTime;
    }

    private void HandleKill()
    {
        if (!_episodeDone && !_episodeTruncated)
        {
            _lastReward -= 1.0f;
            _episodeTruncated = true; // e.g., fell off the map
        }
    }

    // Physics & capture

    void FixedUpdate()
    {
        // Apply the latest action mailbox (passive loop)
        if (carController != null)
        {
            carController.SetInputs(_steerMailbox, _throttleMailbox);
        }

        // Compute reward for this physics tick
        float r = 0f;
        r += aliveReward;

        if (carRb != null)
        {
            // forward speed along the car's forward axis
            Vector3 v = carRb.velocity;
            float forwardSpeed = Vector3.Dot(v, carRb.transform.forward);
            r += Mathf.Max(0f, forwardSpeed) * forwardSpeedScale;
        }

        if (_offRoadTimeAccum > 1e-6f)
        {
            r -= offRoadPenaltyPerSec * _offRoadTimeAccum;
            _offRoadTimeAccum = 0f;
        }

        _lastReward = r;

        // Step count & auto-truncation
        _stepCount++;
        if (maxSteps > 0 && _stepCount >= maxSteps && !_episodeDone && !_episodeTruncated)
        {
            _episodeTruncated = true;
        }
    }

    void LateUpdate()
    {
        // Handle reset request from network
        if (_resetRequested)
        {
            _resetRequested = false;
            BeginNewEpisode();
        }

        // Capture the current frame to JPEG (main thread only)
        if (captureCamera == null || _rt == null || _readTex == null) return;

        var prev = RenderTexture.active;
        RenderTexture.active = _rt;

        // ensure camera rendered
        captureCamera.Render();

        _readTex.ReadPixels(new Rect(0, 0, outputWidth, outputHeight), 0, 0, false);
        _readTex.Apply(false, false);

        byte[] jpg = _readTex.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100));
        RenderTexture.active = prev;

        lock (_jpegLock)
        {
            _jpegBytes = jpg;
            _jpegLen = jpg?.Length ?? 0;
            _jpegStepIndex = _stepCount;
        }
    }

    private void BeginNewEpisode()
    {
        // Reset episode counters and flags
        _stepCount = 0;
        _episodeDone = false;
        _episodeTruncated = false;
        _lastReward = 0f;
        _offRoadTimeAccum = 0f;

        // Reset triggers
        if (finalGoal != null) finalGoal.ResetForNewEpisode();
        if (minorGoals != null)
        {
            foreach (var mg in minorGoals) if (mg != null) mg.ResetForNewEpisode();
        }
        if (offRoad != null) offRoad.ResetForNewEpisode();
        if (killZone != null) killZone.ResetForNewEpisode();

        // Reset car controller if it supports a reset
        if (carController != null) carController.ResetVehicle();
    }

    // Networking

    private void StartNetworkThread()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Debug.Log($"[RLClientSender] listening on 0.0.0.0:{port}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[RLClientSender] Failed to start listener: {e}");
            return;
        }

        _netThread = new Thread(NetLoop) { IsBackground = true, Name = "RLClientSender.NetLoop" };
        _netThread.Start();
    }

    private void StopNetworkThread()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;

        if (_netThread != null)
        {
            try { _netThread.Join(500); } catch { }
            _netThread = null;
        }
        _clientConnected = false;
    }

    private static float ReadBEFloat(NetworkStream s)
    {
        byte[] buf = ReadExact(s, 4);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf);
        return BitConverter.ToSingle(buf, 0);
    }

    private static void WriteBEFloat(NetworkStream s, float x)
    {
        byte[] buf = BitConverter.GetBytes(x);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf);
        s.Write(buf, 0, 4);
    }

    private static void WriteBEU32(NetworkStream s, uint x)
    {
        byte[] buf = BitConverter.GetBytes(x);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf);
        s.Write(buf, 0, 4);
    }

    private static byte[] ReadExact(NetworkStream s, int n)
    {
        byte[] buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int got = s.Read(buf, off, n - off);
            if (got <= 0) throw new Exception("socket closed");
            off += got;
        }
        return buf;
    }

    private void NetLoop()
    {
        while (!_shutdown)
        {
            TcpClient client = null;
            try
            {
                client = _listener.AcceptTcpClient();
                client.NoDelay = true;
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;
                _clientConnected = true;
                Debug.Log("[RLClientSender] client connected");

                using (var stream = client.GetStream())
                {
                    // episode loop
                    while (!_shutdown && client.Connected)
                    {
                        // Wait for reset
                        int b = stream.ReadByte();
                        if (b == -1) break;
                        if (b != (byte)'R')
                        {
                            Debug.LogWarning("[RLClientSender] Unexpected byte, waiting for 'R'.");
                            continue;
                        }

                        // Request a new episode on main thread
                        _resetRequested = true;

                        // Give main thread a moment to run BeginNewEpisode and produce a frame
                        Thread.Sleep(20);

                        // Send first payload after reset
                        SendLatestPayload(stream);

                        // step loop
                        while (!_shutdown && client.Connected)
                        {
                            // Receive action (steer, throttle) as BE float32
                            float steer = ReadBEFloat(stream);
                            float throttle = ReadBEFloat(stream);

                            // Write to mailbox for main thread to apply in FixedUpdate
                            _steerMailbox = Mathf.Clamp(steer, -1f, 1f);
                            _throttleMailbox = Mathf.Clamp01(throttle);

                            // Send next payload
                            SendLatestPayload(stream);

                            if (_episodeDone || _episodeTruncated)
                            {
                                // End of episode: break to wait for next 'R'
                                break;
                            }
                        }
                    }
                }
            }
            catch (ThreadAbortException) { }
            catch (Exception e)
            {
                if (!_shutdown)
                    Debug.LogWarning($"[RLClientSender] NetLoop exception: {e.Message}");
            }
            finally
            {
                try { client?.Close(); } catch { }
                _clientConnected = false;
            }
        }
    }

    private void SendLatestPayload(NetworkStream stream)
    {
        // Snapshot shared fields under lock for consistency
        byte[] jpg;
        int len;
        int stepIdx;
        lock (_jpegLock)
        {
            jpg = _jpegBytes ?? Array.Empty<byte>();
            len = _jpegLen;
            stepIdx = _jpegStepIndex;
        }

        // Header: length (u32 BE)
        WriteBEU32(stream, (uint)len);
        // JPEG bytes
        if (len > 0) stream.Write(jpg, 0, len);
        // Tail: reward (f32 BE), done (u8), truncated (u8)
        WriteBEFloat(stream, _lastReward);
        stream.WriteByte(_episodeDone ? (byte)1 : (byte)0);
        stream.WriteByte(_episodeTruncated ? (byte)1 : (byte)0);
        stream.Flush();
    }

    // Debug overlay (optional)

    void OnGUI()
    {
        // Simple optional one-liner for quick debugging; toggle or remove if you use a HUD.
        var rect = new Rect(10, 10, 600, 20);
        string status = _clientConnected ? "client connected" : "listening";
        GUI.Label(rect, $"RLClientSender: {status} | Step={_stepCount} Reward={_lastReward:+0.000;-0.000} MaxSteps={maxSteps} JPEG={jpegQuality}");
    }
}