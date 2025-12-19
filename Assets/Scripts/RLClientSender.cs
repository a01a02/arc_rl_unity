/*
 * RLClientSender.cs
 * -----------------
 * ROBOTICS PROTOCOL VERSION:
 * - Implements "Passive Visual" Protocol:
 * - Sends High-Level Commands (Left/Right/Follow) in _goalCos slot.
 * - Sends '0' in _goalSin slot (prevents geometric cheating).
 * - Includes Threading Fixes for Python sync.
 * - Includes CaptureFrame method.
 * - Includes AgentViewPIP integration for debugging.
 * 
 * CONTROLLER SUPPORT:
 * - Supports both AckermannSteeringController (recommended for sim-to-real)
 * - And AdvancedDoubleTrackController (for research/comparison)
 * - Ackermann is checked first; falls back to DoubleTrack if not assigned.
 */

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

// High-Level Navigation Command Enum
public enum NavCommand { Follow = 0, Left = -1, Right = 1 }

[DisallowMultipleComponent]
public class RLClientSender : MonoBehaviour
{
    // ======== Network ========
    [Header("Network")]
    public int port = 5556;
    [Tooltip("Socket read/write timeouts (ms).")]
    public int socketTimeoutMs = 15000;

    // ======== Capture ========
    [Header("Capture")]
    public Camera captureCamera;
    public int outputWidth = 128;
    public int outputHeight = 128;
    [Range(1, 100)] public int jpegQuality = 80;

    [Header("Camera Mount (capture only)")]
    [Tooltip("Attach capture camera to the car root every frame (for Mac/Editor).")]
    public bool lockCameraToCar = true;
    public Vector3 camLocalPos = new Vector3(0f, 1.10f, 0f);
    public Vector3 camLocalEuler = new Vector3(10f, 0f, 0f);
    public bool zeroRoll = true;
    [Tooltip("If true, force FOV on the capture camera.")]
    public bool lockCameraIntrinsics = true;
    public float cameraFov = 60f;

    // ======== Control (mailbox) ========
    [Header("Control (read-only)")]
    [SerializeField, Range(-1f, 1f)] private float _lastSteerCmd = 0f;
    [SerializeField, Range(0f, 1f)]  private float _lastThrottleCmd = 0f;
    [SerializeField, Range(0f, 1f)]  private float _lastBrakeCmd = 0f;
    public float LastSteer    => _lastSteerCmd;
    public float LastThrottle => _lastThrottleCmd;
    public float LastBrake    => _lastBrakeCmd;

    // ======== Safety ========
    [Header("Safety")]
    [Tooltip("If no action arrives for this long, throttle is forced to 0.")]
    public float commandTimeoutSec = 0.75f;

    [Tooltip("Keep a tiny throttle right after reset to guarantee rolling even if first action is late.")]
    public bool  startupKick = true;
    [Range(0f, 1f)] public float startupKickThrottle = 0.18f;
    public float startupKickDurationSec = 0.50f;

    [Tooltip("If true, completely freeze vehicle when no Python client is connected.")]
    public bool freezeWhenNoClient = true;

    // ======== Episode ========
    [Header("Episode")]
    [Tooltip("Max physics steps before truncation (0 = unlimited).")]
    public int maxSteps = 500;

    [Header("Episode Randomization")]
    [Tooltip("If true, add small position/yaw jitter on each reset (F3 toggles at runtime).")]
    public bool randomizeOnReset = false;
    public Transform spawnAnchor;
    public float spawnYawJitterDeg = 10f;
    public float spawnPosJitterM = 0.5f;

    private Vector3 _spawnPos;
    private Quaternion _spawnRot;

    // ======== References ========
    [Header("References")]
    public Rigidbody carRb;

    [Tooltip("Preferred: Ackermann controller for sim-to-real (recommended).")]
    public AckermannDriveController ackermannController;

    [Tooltip("Alternative: Double-track controller for research (fallback if Ackermann not assigned).")]
    public AdvancedDoubleTrackController doubleTrackController;

    public RouteProgress routeProgress;

    public Rigidbody CarBody => carRb;
    
    // CarRoot prioritizes Ackermann, then DoubleTrack, then raw Rigidbody
    public Transform CarRoot =>
        (ackermannController != null ? ackermannController.transform :
         (doubleTrackController != null ? doubleTrackController.transform :
          (carRb != null ? carRb.transform : null)));

    // Helper to check which controller is active
    public bool UsingAckermann => ackermannController != null;
    public bool UsingDoubleTrack => ackermannController == null && doubleTrackController != null;

    // ======== Terminations / Goals ========
    [Header("Terminations / Goals")]
    [Tooltip("Preferred: radius-based goal detector.")]
    public GoalProximity finalGoalProx;

    [Tooltip("Optional: kill/fall zone. Ends episode with -1.")]
    public KillZone killZone;
    [Tooltip("Optional fallback Transform if no GoalProximity is set.")]
    public Transform goalCenterFallback;

    // ======== Visuals ========
    [Header("HUD")]
    public bool showTopHUD = true;
    public bool showBottomHUD = true;

    [Header("Hotkeys")]
    public KeyCode toggleTopHUDKey = KeyCode.F1;
    public KeyCode toggleBottomHUDKey = KeyCode.F2;
    public KeyCode toggleRandomizeKey = KeyCode.F3;

    [Header("Waypoint Visualization")]
    [Tooltip("Visualizes predicted waypoints from hierarchical policy.")]
    public LearnedPathVisualizer pathVisualizer;
    
    [Tooltip("Optional: Raw Image to display Agent's View for debugging.")]
    public UnityEngine.UI.RawImage agentViewPIP;

    // ======== Internal state ========
    private int   _stepCount = 0;
    private bool  _episodeDone = false;
    private bool  _episodeTruncated = false;
    private float _lastReward = 0f;
    private volatile bool _episodeActive = false;

    public int   StepCount        => _stepCount;
    public float LastReward       => _lastReward;
    public bool  EpisodeDone      => _episodeDone;
    public bool  EpisodeTruncated => _episodeTruncated;

    public int EpisodeId { get; private set; } = 0;
    public event System.Action OnEpisodeReset;

    // Networking
    private TcpListener _listener;
    private Thread _netThread;
    private volatile bool _shutdown = false;
    private volatile bool _clientConnected = false;

    public bool IsListening       => _listener != null;
    public bool IsClientConnected => _clientConnected;

    // Action mailbox
    private volatile float _steerMailbox = 0f;
    private volatile float _throttleMailbox = 0f;
    private volatile float _brakeMailbox = 0f;
    private volatile bool _resetRequested = false;

    // Timing
    private static readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private long _lastCmdMs = long.MinValue;
    private long _episodeStartMs = 0;

    // Capture resources
    private RenderTexture _rt;
    private Texture2D _readTex;

    // Shared JPEG buffer
    private readonly object _jpegLock = new object();
    private byte[] _jpegBytes = Array.Empty<byte>();
    private int _jpegLen = 0;

    // Shared goal telemetry (The Critical Passive Visual Section)
    private readonly object _telemetryLock = new object();
    private float _goalCos = 0f, _goalSin = 0f, _goalDistXZ = -1f;

    // Shared route metrics
    private readonly object _routeLock = new object();
    private float _latErr = 0f, _hdgErr = 0f, _kappa = 0f, _dsRoute = 0f;

    // Shared proprio cache
    private readonly object _propLock = new object();
    private float _speedCached = 0f;
    private float _yawRateCached = 0f;

    // Frame-counting
    private volatile int _frameNumber = 0;

    // ======== Unity lifecycle ========

    void Awake()
    {
        InitCamera();
        InitSpawn();
        InitGoalListeners();
        
        // Log which controller is being used
        if (ackermannController != null)
        {
            Debug.Log("[RLClientSender] Using AckermannSteeringController (recommended for sim-to-real)");
        }
        else if (doubleTrackController != null)
        {
            Debug.Log("[RLClientSender] Using AdvancedDoubleTrackController (research mode)");
        }
        else
        {
            Debug.LogWarning("[RLClientSender] No controller assigned! Using raw Rigidbody fallback.");
        }
    }

    void Start()
    {
        StartListener();
    }

    void OnDestroy()
    {
        StopListener();
        Cleanup();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleTopHUDKey)) showTopHUD = !showTopHUD;
        if (Input.GetKeyDown(toggleBottomHUDKey)) showBottomHUD = !showBottomHUD;
        if (Input.GetKeyDown(toggleRandomizeKey))
        {
            randomizeOnReset = !randomizeOnReset;
            Debug.Log($"[RLClientSender] Randomization: {randomizeOnReset}");
        }

        if (_resetRequested)
        {
            _resetRequested = false;
            ResetEpisode();
        }
    }

    void FixedUpdate()
    {
        float s = _steerMailbox;
        float t = _throttleMailbox;
        float b = _brakeMailbox;

        long dt = _wall.ElapsedMilliseconds - _lastCmdMs;
        bool isTimeout = (dt > commandTimeoutSec * 1000f);

        if (startupKick)
        {
            long sinceReset = _wall.ElapsedMilliseconds - _episodeStartMs;
            if (sinceReset < startupKickDurationSec * 1000f)
            {
                t = Mathf.Max(t, startupKickThrottle);
                isTimeout = false;
            }
        }

        if (isTimeout)
        {
            t = 0f; 
        }

        _lastSteerCmd = s;
        _lastThrottleCmd = t;
        _lastBrakeCmd = b;

        // Apply inputs to whichever controller is active
        // Priority: Ackermann > DoubleTrack > Raw Rigidbody
        if (ackermannController != null)
        {
            ackermannController.SetInputs(s, t, b);
        }
        else if (doubleTrackController != null)
        {
            doubleTrackController.SetInputs(s, t, b);
        }
        else if (carRb != null)
        {
            // Fallback: direct rigidbody control
            carRb.AddTorque(0f, s * 5f, 0f);
            carRb.AddForce(carRb.transform.forward * (t - b) * 10f);
        }

        if (_episodeActive)
        {
            _stepCount++;
            if (maxSteps > 0 && _stepCount >= maxSteps)
            {
                _episodeTruncated = true;
                _episodeActive = false;
                Debug.Log($"[RLClientSender] Episode truncated at {_stepCount} steps.");
            }
        }

        // Get telemetry from active controller
        float spd = 0f, yaw = 0f;
        if (ackermannController != null)
        {
            spd = ackermannController.Speed;
            yaw = ackermannController.YawRate;
        }
        else if (doubleTrackController != null)
        {
            spd = doubleTrackController.Speed;
            yaw = doubleTrackController.YawRate;
        }
        else if (carRb != null)
        {
            spd = carRb.linearVelocity.magnitude;
            yaw = carRb.angularVelocity.y;
        }
        
        lock (_propLock)
        {
            _speedCached = spd;
            _yawRateCached = yaw;
        }

        if (freezeWhenNoClient && !_clientConnected && carRb != null)
        {
            carRb.linearVelocity = Vector3.zero;
            carRb.angularVelocity = Vector3.zero;
        }
    }

    void LateUpdate()
    {
        if (lockCameraToCar && captureCamera != null && CarRoot != null)
        {
            captureCamera.transform.position = CarRoot.TransformPoint(camLocalPos);
            var rot = CarRoot.rotation * Quaternion.Euler(camLocalEuler);
            if (zeroRoll)
            {
                var e = rot.eulerAngles;
                e.z = 0f;
                rot = Quaternion.Euler(e);
            }
            captureCamera.transform.rotation = rot;
        }

        if (lockCameraIntrinsics && captureCamera != null)
        {
            captureCamera.fieldOfView = cameraFov;
        }

        CaptureFrame();
        UpdateGoalTelemetry();
        UpdateRouteMetrics();
        _frameNumber++;
    }

    // ======== Initialization ========

    private void InitCamera()
    {
        if (captureCamera == null)
        {
            var go = GameObject.Find("CaptureCamera");
            if (go != null) captureCamera = go.GetComponent<Camera>();
        }

        if (captureCamera != null)
        {
            _rt = new RenderTexture(outputWidth, outputHeight, 24);
            _readTex = new Texture2D(outputWidth, outputHeight, TextureFormat.RGB24, false);
            captureCamera.targetTexture = _rt;
            captureCamera.enabled = true;

            // Connect PIP
            if (agentViewPIP != null)
            {
                agentViewPIP.texture = _rt;
                agentViewPIP.color = Color.white;
            }
        }
        else
        {
            Debug.LogWarning("[RLClientSender] No capture camera found!");
        }
    }

    private void InitSpawn()
    {
        if (spawnAnchor == null)
        {
            spawnAnchor = GameObject.Find("SpawnPoint")?.transform;
        }

        if (spawnAnchor != null)
        {
            _spawnPos = spawnAnchor.position;
            _spawnRot = spawnAnchor.rotation;
        }
        else if (CarRoot != null)
        {
            _spawnPos = CarRoot.position;
            _spawnRot = CarRoot.rotation;
        }
        else
        {
            _spawnPos = Vector3.zero;
            _spawnRot = Quaternion.identity;
        }
    }

    private void InitGoalListeners()
    {
        if (finalGoalProx != null)
        {
            finalGoalProx.OnGoalReached += HandleGoalReached;
        }

        if (killZone != null)
        {
            killZone.OnKill += HandleKilled;
        }
    }

    private void Cleanup()
    {
        if (finalGoalProx != null)
        {
            finalGoalProx.OnGoalReached -= HandleGoalReached;
        }

        if (killZone != null)
        {
            killZone.OnKill -= HandleKilled;
        }

        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
        }

        if (_readTex != null)
        {
            Destroy(_readTex);
        }
    }

    // ======== Episode management ========

    private void ResetEpisode()
    {
        string controllerName = ackermannController != null ? "Ackermann" : 
                               (doubleTrackController != null ? "DoubleTrack" : "None");
        Debug.Log($"[RLClientSender] Resetting episode {EpisodeId} (Controller: {controllerName})");

        _stepCount = 0;
        _episodeDone = false;
        _episodeTruncated = false;
        _lastReward = 0f;
        _episodeActive = true;
        _episodeStartMs = _wall.ElapsedMilliseconds;
        EpisodeId++;

        if (CarRoot != null && carRb != null)
        {
            Vector3 pos = _spawnPos;
            Quaternion rot = _spawnRot;

            if (randomizeOnReset)
            {
                pos += new Vector3(
                    UnityEngine.Random.Range(-spawnPosJitterM, spawnPosJitterM),
                    0f,
                    UnityEngine.Random.Range(-spawnPosJitterM, spawnPosJitterM)
                );

                float yawJitter = UnityEngine.Random.Range(-spawnYawJitterDeg, spawnYawJitterDeg);
                rot = rot * Quaternion.Euler(0f, yawJitter, 0f);
            }

            CarRoot.position = pos;
            CarRoot.rotation = rot;

            carRb.linearVelocity = Vector3.zero;
            carRb.angularVelocity = Vector3.zero;
        }

        // Reset the active controller
        if (ackermannController != null) 
        {
            ackermannController.ResetVehicle();
        }
        else if (doubleTrackController != null) 
        {
            doubleTrackController.ResetVehicle();
        }
        
        if (finalGoalProx != null) finalGoalProx.ResetForNewEpisode();
        if (killZone != null) killZone.ResetForNewEpisode();

        _steerMailbox = 0f;
        _throttleMailbox = 0f;
        _brakeMailbox = 0f;

        OnEpisodeReset?.Invoke();
    }

    private void HandleGoalReached()
    {
        if (!_episodeDone && !_episodeTruncated)
        {
            Debug.Log("[RLClientSender] Goal reached!");
            _episodeDone = true;
            _episodeActive = false;
            _lastReward = 10f;
        }
    }

    private void HandleKilled()
    {
        if (!_episodeDone && !_episodeTruncated)
        {
            Debug.Log("[RLClientSender] Vehicle killed!");
            _episodeTruncated = true;
            _episodeActive = false;
            _lastReward = -1f;
        }
    }

    public void ExternalKill()
    {
        HandleKilled();
    }

    // ======== Frame Capture ========

    private void CaptureFrame()
    {
        if (_rt == null || _readTex == null || captureCamera == null) return;

        var oldRT = RenderTexture.active;
        RenderTexture.active = _rt;
        captureCamera.Render();
        _readTex.ReadPixels(new Rect(0, 0, outputWidth, outputHeight), 0, 0);
        _readTex.Apply();
        RenderTexture.active = oldRT;

        var jpg = _readTex.EncodeToJPG(jpegQuality);

        lock (_jpegLock)
        {
            _jpegBytes = jpg;
            _jpegLen = jpg?.Length ?? 0;
        }
    }

    // --- PASSIVE VISUAL NAVIGATION LOGIC ---
    private void UpdateGoalTelemetry()
    {
        float c = 0f, s = 0f, d = -1f;

        // 1. Get Raw Geometry (Needed internally for Rewards)
        if (finalGoalProx != null)
        {
            finalGoalProx.GetDistances(out c, out s, out d);
        }
        else if (goalCenterFallback != null && CarRoot != null)
        {
            Vector3 p = CarRoot.position;
            Vector3 g = goalCenterFallback.position;

            Vector2 fwd = new Vector2(CarRoot.forward.x, CarRoot.forward.z).normalized;
            Vector2 dir = new Vector2(g.x - p.x, g.z - p.z);
            d = dir.magnitude;

            if (d > 1e-6f)
            {
                dir = dir.normalized;
                c = Vector2.Dot(fwd, dir);
                float cross = fwd.x * dir.y - fwd.y * dir.x;
                s = Mathf.Sign(cross) * Mathf.Sqrt(Mathf.Max(0f, 1f - c * c));
            }
            else
            {
                c = 1f; s = 0f; d = 0f;
            }
        }

        // 2. Calculate Command (Intent)
        float command = (float)NavCommand.Follow;
        
        if (d > 15.0f) 
        {
            // If goal is to the left (sin > 0.4), command Left
            if (s > 0.4f) command = (float)NavCommand.Left;
            // If goal is to the right (sin < -0.4), command Right
            else if (s < -0.4f) command = (float)NavCommand.Right;
        }

        lock (_telemetryLock)
        {
            // PROTOCOL:
            // Slot 1 (GoalCos) -> Command (-1, 0, 1)
            // Slot 2 (GoalSin) -> 0 (Unused/Hidden)
            // Slot 3 (GoalDist) -> Distance (Used for reward only)
            
            _goalCos = command;
            _goalSin = 0f;
            _goalDistXZ = d;
        }
    }

    private void UpdateRouteMetrics()
    {
        float lat = 0f, hdg = 0f, kap = 0f, ds = 0f;

        if (routeProgress != null)
        {
            lat = routeProgress.LateralErrorMeters;
            hdg = routeProgress.HeadingErrorRad;
            kap = routeProgress.PathCurvature;
            ds = routeProgress.DeltaSPerStep;
        }

        lock (_routeLock)
        {
            _latErr = lat;
            _hdgErr = hdg;
            _kappa = kap;
            _dsRoute = ds;
        }
    }

    // ======== Networking ========

    private void StartListener()
    {
        if (_listener != null) return;

        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(1);

            _shutdown = false;
            _netThread = new Thread(NetLoop) { IsBackground = true };
            _netThread.Start();

            Debug.Log($"[RLClientSender] Listening on port {port}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[RLClientSender] Failed to start listener: {e.Message}");
            _listener = null;
        }
    }

    private void StopListener()
    {
        _shutdown = true;

        try
        {
            _listener?.Stop();
            _netThread?.Join(100);
        }
        catch { }

        _listener = null;
        _netThread = null;
        _clientConnected = false;
    }

    private void NetLoop()
    {
        while (!_shutdown)
        {
            TcpClient client = null;
            try
            {
                if (!_listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                client = _listener.AcceptTcpClient();
                client.ReceiveTimeout = socketTimeoutMs;
                client.SendTimeout = socketTimeoutMs;
                client.NoDelay = true;

                _clientConnected = true;
                Debug.Log("[RLClientSender] client connected");

                using (var stream = client.GetStream())
                {
                    int lastSentFrame = -1;
                    bool cameFromResetInActionLoop = false; 

                    while (!_shutdown && client.Connected)
                    {
                        if (!cameFromResetInActionLoop)
                        {
                            int b;
                            do
                            {
                                b = stream.ReadByte();
                                if (b == -1) break; 

                                if (b == (byte)'W')
                                {
                                    HandleWaypointMessage(stream);
                                    continue; 
                                }
                            } while (b != (byte)'R');

                            if (b == -1) break;
                        }

                        cameFromResetInActionLoop = false;
                        _resetRequested = true;
                        
                        // CRITICAL: Wait for main thread to process reset
                        Thread.Sleep(50);

                        _lastCmdMs = _wall.ElapsedMilliseconds;
                        WaitForNewFrame(ref lastSentFrame, 500);
                        SendLatestPayload(stream);

                        while (!_shutdown && client.Connected)
                        {
                            int msgType = stream.ReadByte();
                            if (msgType == -1) break; 

                            if (msgType == (byte)'R')
                            {
                                cameFromResetInActionLoop = true;
                                _resetRequested = true;
                                _lastCmdMs = _wall.ElapsedMilliseconds;
                                break; 
                            }
                            else if (msgType == (byte)'W')
                            {
                                HandleWaypointMessage(stream);
                                continue; 
                            }
                            else if (msgType == (byte)'A')
                            {
                                float steer    = ReadLEFloat(stream);
                                float throttle = ReadLEFloat(stream);
                                float brake    = ReadLEFloat(stream);

                                if (float.IsNaN(steer)    || float.IsInfinity(steer))    steer    = 0f;
                                if (float.IsNaN(throttle) || float.IsInfinity(throttle)) throttle = 0f;
                                if (float.IsNaN(brake)    || float.IsInfinity(brake))    brake    = 0f;

                                _steerMailbox    = Mathf.Clamp(steer, -1f, 1f);
                                _throttleMailbox = Mathf.Clamp01(throttle);
                                _brakeMailbox    = Mathf.Clamp01(brake);
                                _lastCmdMs = _wall.ElapsedMilliseconds; 

                                if (!WaitForNewFrame(ref lastSentFrame, 250))
                                {
                                    // timeout fallback
                                }

                                SendLatestPayload(stream);

                                if (_episodeDone || _episodeTruncated)
                                {
                                    _episodeActive = false; 
                                    break;
                                }
                            }
                            else if (msgType == (byte)'Q')
                            {
                                Debug.Log("[RLClientSender] Quit command received");
                                _shutdown = true;
                                break;
                            }
                            else
                            {
                                Debug.LogWarning($"[RLClientSender] Unknown message type: {(char)msgType}");
                                break; 
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!_shutdown)
                {
                    string msg = e.Message ?? "";
                    if (msg.IndexOf("non-blocking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                        Debug.Log($"[RLClientSender] NetLoop note: {msg}");
                    else
                        Debug.LogWarning($"[RLClientSender] NetLoop exception: {msg}");
                }
            }
            finally
            {
                try { client?.Close(); } catch { }
                _clientConnected = false;
            }
        }
    }

    private void HandleWaypointMessage(NetworkStream stream)
    {
        int numWaypoints = stream.ReadByte();
        if (numWaypoints == -1 || numWaypoints > 20) return;

        float[] waypoints = new float[numWaypoints * 2];
        for (int i = 0; i < numWaypoints * 2; i++)
        {
            waypoints[i] = ReadLEFloat(stream);
        }

        if (pathVisualizer != null && numWaypoints > 0)
        {
            pathVisualizer.UpdateWaypoints(waypoints, numWaypoints);
        }
    }

    private bool WaitForNewFrame(ref int lastFrame, int timeoutMs)
    {
        long start = _wall.ElapsedMilliseconds;
        while (_frameNumber <= lastFrame)
        {
            if (_wall.ElapsedMilliseconds - start > timeoutMs)
                return false;
            Thread.Sleep(5);
        }
        lastFrame = _frameNumber;
        return true;
    }

    private void SendLatestPayload(NetworkStream stream)
    {
        byte[] jpg; int len;
        float goalCos, goalSin, goalDist;
        float rew; bool done, trunc;
        float lat, hdg, kap, ds;
        float spd, yaw;

        lock (_jpegLock) { jpg = _jpegBytes ?? Array.Empty<byte>(); len = _jpegLen; }
        lock (_telemetryLock) { goalCos = _goalCos; goalSin = _goalSin; goalDist = _goalDistXZ; }
        lock (_routeLock) { lat = _latErr; hdg = _hdgErr; kap = _kappa; ds = _dsRoute; }
        lock (_propLock) { spd = _speedCached; yaw = _yawRateCached; }

        rew   = _lastReward;
        done  = _episodeDone;
        trunc = _episodeTruncated;

        WriteBEU32(stream, (uint)len);
        if (len > 0) stream.Write(jpg, 0, len);

        // Send Command (Cos slot), 0 (Sin slot), Dist
        WriteBEFloat(stream, goalCos);
        WriteBEFloat(stream, goalSin);
        WriteBEFloat(stream, goalDist);

        WriteBEFloat(stream, spd);
        WriteBEFloat(stream, yaw);
        WriteBEFloat(stream, _lastSteerCmd);
        WriteBEFloat(stream, _lastThrottleCmd);
        WriteBEFloat(stream, _lastBrakeCmd);

        WriteBEFloat(stream, lat);
        WriteBEFloat(stream, hdg);
        WriteBEFloat(stream, kap);
        WriteBEFloat(stream, ds);

        WriteBEFloat(stream, rew);
        stream.WriteByte(done ? (byte)1 : (byte)0);
        stream.WriteByte(trunc ? (byte)1 : (byte)0);
        stream.Flush();
    }

    private static void WriteBEU32(NetworkStream s, uint val)
    {
        s.WriteByte((byte)(val >> 24)); s.WriteByte((byte)(val >> 16));
        s.WriteByte((byte)(val >> 8)); s.WriteByte((byte)val);
    }

    private static void WriteBEFloat(NetworkStream s, float val)
    {
        byte[] b = BitConverter.GetBytes(val);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        s.Write(b, 0, 4);
    }

    private static float ReadLEFloat(NetworkStream s)
    {
        byte[] b = new byte[4];
        s.Read(b, 0, 4);
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        return BitConverter.ToSingle(b, 0);
    }

    void OnGUI()
    {
        GUI.depth = 0;
        var black = new GUIStyle(GUI.skin.label);
        black.normal.textColor = Color.black;
        black.alignment = TextAnchor.UpperLeft;
        black.fontSize = 14;

        if (showTopHUD)
        {
            const int w = 980, h = 24;
            GUI.Box(new Rect(8, 8, w, h), GUIContent.none);
            string status = _clientConnected ? "client connected" : "listening";
            string controller = ackermannController != null ? "Ackermann" : 
                               (doubleTrackController != null ? "DoubleTrack" : "None");
            GUI.Label(new Rect(12, 12, w - 8, h),
                $"RLClientSender: {status} | Controller: {controller} | Step={_stepCount} Reward={_lastReward:+0.000;-0.000} Done={_episodeDone} Trunc={_episodeTruncated}", black);
        }

        if (showBottomHUD)
        {
            const int w = 1280, h = 40;
            int y = Screen.height - h - 10;
            GUI.Box(new Rect(8, y, w, h), GUIContent.none);

            float cmd, zero, d;
            lock (_telemetryLock) { cmd = _goalCos; zero = _goalSin; d = _goalDistXZ; }
            float spd, yaw;
            lock (_propLock) { spd = _speedCached; yaw = _yawRateCached; }

            string cmdStr = "Follow";
            if (Mathf.Approximately(cmd, -1f)) cmdStr = "Left";
            else if (Mathf.Approximately(cmd, 1f)) cmdStr = "Right";

            GUI.Label(new Rect(12, y + 6, w - 8, h - 6),
                $"Command: {cmdStr} (dist={d:0.0}m)   |   " +
                $"Act: steer={_lastSteerCmd:+0.00;-0.00}  thr={_lastThrottleCmd:0.00}  brk={_lastBrakeCmd:0.00}   |   " +
                $"Speed={spd:0.00} m/s  YawRate={yaw:+0.00;-0.00} rad/s", black);
        }
    }
}