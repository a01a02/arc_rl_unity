using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

[DefaultExecutionOrder(-50)]
public class RLClientSender : MonoBehaviour
{
    [Header("Network")]
    public int port = 5556;

    [Header("Capture")]
    public Camera captureCamera;
    [Range(16, 512)] public int outputWidth = 84;
    [Range(16, 512)] public int outputHeight = 84;
    [Range(10, 100)] public int jpegQuality = 80;

    [Header("Episode")]
    public int maxSteps = 500;
    public Transform startPoint;                  // where the car respawns on reset
    public SimpleCarController controller;        // your driving script
    public Rigidbody rb;                          // rigidbody on the car

    [Header("Route / Goals")]
    public RoutePath routePath;                   // empty with child waypoints (start -> goal)
    public GoalTrigger finalGoal;                 // destination trigger (calls SharedEpisodeFlags.SetGoal)
    public MinorGoalTrigger[] minorGoalsToReset;  // all minor goals to re-arm each episode

    [Header("Reward Shaping")]
    public float aliveReward = 0.01f;
    public float forwardVelScale = 0.02f;
    [Range(0f, 2f)] public float progressRewardScale = 0.6f;
    [Range(0f, 1f)] public float headingPenaltyScale = 0.08f;
    [Range(0f, 0.2f)] public float offRoadPenaltyPerStep = 0.03f;

	[Header("Goal Rewards")]
	public float minorGoalBonus = 0.3f;
	private int _lastMinorCount = 0;

    [Header("Bounds / Kill")]
    public Vector2 mapMinXZ = new Vector2(-50f, -50f);
    public Vector2 mapMaxXZ = new Vector2( 50f,  50f);

	[Header("Network Timing")]
	[Range(1, 50)] public int netTickMs = 16; // sleep time for NetLoop (ms)

    // --- internal ---
    private TcpListener _listener;
    private Thread _netThread;
    private volatile bool _running;
    private volatile bool _clientConnected;

    private Texture2D _readbackTex;
    private RenderTexture _rt;

    private byte[] _latestJpeg = Array.Empty<byte>();
    private readonly object _jpegLock = new object();

    private volatile float _pendingSteer = 0f;
    private volatile float _pendingThrottle = 0f;
    private volatile bool _havePendingAction = false;

    private int _stepCount = 0;
    private volatile float _lastReward = 0f;
    private volatile byte _lastDone = 0;
    private volatile byte _lastTruncated = 0;

    private RouteProgress _route;

    private static RLClientSender _singleton;

    // queue for running actions on main thread
    private readonly Queue<Action> _mainQueue = new Queue<Action>();

    // -------------------- Unity Lifecycle --------------------

    void Awake()
    {
        // Avoid duplicates
        if (_singleton != null && _singleton != this)
        {
            Debug.LogWarning($"RLClientSender duplicate on '{gameObject.name}' — disabling this one.");
            enabled = false;
            return;
        }
        _singleton = this;

        Application.runInBackground = true;

        // Auto-assign camera/controller/rigidbody if not set
        if (captureCamera == null)
        {
            captureCamera = GetComponentInChildren<Camera>(true);
            if (captureCamera == null) captureCamera = Camera.main;
            if (captureCamera == null)
            {
                var cams = FindObjectsOfType<Camera>(true);
                if (cams.Length > 0) captureCamera = cams[0];
            }
        }
        if (controller == null) controller = FindObjectOfType<SimpleCarController>();
        if (controller != null && rb == null) rb = controller.GetComponent<Rigidbody>();

        if (captureCamera == null) { Debug.LogError("RLClientSender: captureCamera not set."); enabled = false; return; }
        if (controller == null)    { Debug.LogError("RLClientSender: controller not set.");    enabled = false; return; }
        if (rb == null)            { Debug.LogError("RLClientSender: Rigidbody not found.");   enabled = false; return; }

        // Route helper
        if (routePath != null && routePath.waypoints != null && routePath.waypoints.Length >= 2)
            _route = new RouteProgress(routePath.waypoints);

        // RT + readback
        _rt = new RenderTexture(outputWidth, outputHeight, 16, RenderTextureFormat.ARGB32);
        _rt.Create();
        captureCamera.targetTexture = _rt;
        _readbackTex = new Texture2D(outputWidth, outputHeight, TextureFormat.RGB24, false);
    }

    void Start()
    {
        // Start TCP server
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
        try { _listener?.Stop(); } catch { }
        if (_netThread != null && _netThread.IsAlive)
        {
            try { _netThread.Join(250); } catch { }
            if (_netThread.IsAlive) _netThread.Interrupt();
        }
        if (captureCamera != null) captureCamera.targetTexture = null;
        try { _rt?.Release(); } catch { }
        if (_singleton == this) _singleton = null;
    }

    void OnApplicationQuit() => _running = false;

    // -------------------- Episode Control --------------------

    private void ResetEpisode(bool hardResetPose = true)
    {
        SharedEpisodeFlags.ResetFlags();
        _stepCount = 0;
        _lastReward = 0f;
        _lastDone = 0;
        _lastTruncated = 0;
		_lastMinorCount = 0;
        _havePendingAction = false;
        _pendingSteer = 0f;
        _pendingThrottle = 0f;

        if (_route != null) _route.Reset();

        if (finalGoal != null) finalGoal.ResetForNewEpisode();
        if (minorGoalsToReset != null)
        {
            foreach (var g in minorGoalsToReset)
                if (g != null) g.ResetForNewEpisode();
        }

        if (hardResetPose && startPoint != null && controller != null)
        {
            var t = controller.transform;
            t.position = startPoint.position;
            t.rotation = startPoint.rotation;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            SafeSetInputs(0f, 0f);
        }
    }

    // -------------------- Main Loops --------------------

    IEnumerator MainLoop()
    {
        var eof = new WaitForEndOfFrame();

        // Prepare first episode so first 'R' returns a valid frame
        ResetEpisode(true);

        while (_running)
        {
            // drain main-thread jobs (e.g., reset from NetLoop)
            lock (_mainQueue)
            {
                while (_mainQueue.Count > 0)
                    _mainQueue.Dequeue()?.Invoke();
            }

            // physics/control
            yield return new WaitForFixedUpdate();

            if (_clientConnected)
            {
                if (_havePendingAction)
                {
                    SafeSetInputs(_pendingSteer, _pendingThrottle);
                    _havePendingAction = false;
                }

                bool done, truncated;
                float r = ComputeRewardAndDone(out done, out truncated);
                _lastReward = r;
                _lastDone = (byte)(done ? 1 : 0);
                _lastTruncated = (byte)(truncated ? 1 : 0);

                _stepCount++;
                if (_stepCount >= Mathf.Max(1, maxSteps) && _lastDone == 0 && _lastTruncated == 0)
                    _lastTruncated = 1;
            }

            // capture after render
            yield return eof;
            CaptureIntoJpeg();
        }
    }

    private void CaptureIntoJpeg()
    {
        if (captureCamera == null || _readbackTex == null || _rt == null) return;

        var prev = RenderTexture.active;
        try
        {
            captureCamera.Render();
            RenderTexture.active = _rt;
            _readbackTex.ReadPixels(new Rect(0, 0, outputWidth, outputHeight), 0, 0, false);
            _readbackTex.Apply(false, false);
            byte[] jpg = _readbackTex.EncodeToJPG(Mathf.Clamp(jpegQuality, 10, 100));
            lock (_jpegLock) { _latestJpeg = jpg; }
        }
        catch (Exception e)
        {
            Debug.LogWarning("RLClientSender capture error: " + e.Message);
        }
        finally
        {
            RenderTexture.active = prev;
        }
    }

    private void SafeSetInputs(float steer, float throttle)
	{
    	// Try a direct SetInputs(steer, throttle) method first
    	try
    	{
        	var mi = controller.GetType().GetMethod(
            	"SetInputs",
            	BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            	null,
            	new Type[] { typeof(float), typeof(float) },
            	null
        	);
        	if (mi != null)
        	{
            	mi.Invoke(controller, new object[] { steer, throttle });
            	return;
        	}
    	}
    	catch { /* ignore and try fallbacks */ }

    	// Fallbacks: try common field/property names
    	if (!TrySetMember(controller, "steerInput", steer))
        	if (!TrySetMember(controller, "steer", steer))
            	TrySetMember(controller, "Steer", steer); // extra fallback (capitalized)

    	if (!TrySetMember(controller, "throttleInput", throttle))
        	if (!TrySetMember(controller, "throttle", throttle))
            	TrySetMember(controller, "Throttle", throttle); // extra fallback (capitalized)
	}

    private bool TrySetMember(object obj, string name, float value)
    {
        try
        {
            var t = obj.GetType();
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float)) { f.SetValue(obj, value); return true; }
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite && p.PropertyType == typeof(float)) { p.SetValue(obj, value, null); return true; }
        }
        catch {}
        return false;
    }

    // -------------------- Reward & Termination --------------------

    private float ComputeRewardAndDone(out bool done, out bool truncated)
    {
        done = false;
        truncated = false;

        float r = 0f;

        // 1) alive + forward velocity along car forward
        float vForward = Vector3.Dot(rb.velocity, controller.transform.forward);
        r += aliveReward;
        r += forwardVelScale * Mathf.Max(0f, vForward);

        // 2) route progress + heading alignment
        if (_route != null)
        {
            Vector3 segDir;
            float progressDelta = _route.Update(controller.transform.position, out segDir); // 0..1
            r += progressRewardScale * progressDelta;

            float headingErr = Vector3.SignedAngle(controller.transform.forward, segDir, Vector3.up);
            float normHeadingErr = Mathf.Min(1f, Mathf.Abs(headingErr) / 90f); // ~1 at 90 deg
            r -= headingPenaltyScale * normHeadingErr * Time.fixedDeltaTime;
			// small lateral penalty to hug the centerline
			r -= 0.02f * (_route != null ? _route.LastLateralMeters : 0f) * Time.fixedDeltaTime;
        }

        // 3) off-road penalty (set by OffRoadTrigger)
        if (SharedEpisodeFlags.OffRoadContact)
            r -= offRoadPenaltyPerStep;

		int m = SharedEpisodeFlags.MinorGoalsReached;
		if (m > _lastMinorCount) {
    		r += minorGoalBonus * (m - _lastMinorCount);
    		_lastMinorCount = m;
		}

        // 4) final goal reached
        if (SharedEpisodeFlags.GoalReached)
        {
            r += SharedEpisodeFlags.GoalBonus;
            done = true;
            return r;
        }

        // 5) kill zone
        if (SharedEpisodeFlags.KillNow)
        {
            r -= 1.0f;
            done = true;
            return r;
        }

        // 6) out-of-bounds
        Vector3 p = controller.transform.position;
        if (p.x < mapMinXZ.x || p.x > mapMaxXZ.x || p.z < mapMinXZ.y || p.z > mapMaxXZ.y)
        {
            r -= 1.0f;
            truncated = true;
            return r;
        }

        return r;
    }

    // -------------------- Networking --------------------

    private void NetLoop()
	{
    	try
    	{
        	while (_running)
        	{
            	using (var client = _listener.AcceptTcpClient())
            	using (var ns = client.GetStream())
            	{
                	_clientConnected = true;
                	ns.ReadTimeout = 20000;
                	ns.WriteTimeout = 20000;

                	// Wait for 'R' (reset) from client
                	int b = ns.ReadByte();
                	while (b != 'R')
                	{
                    	if (b < 0) throw new Exception("Client closed before reset");
                    	b = ns.ReadByte();
                	}

                	// Ask main thread to reset episode (don’t touch Unity here)
                	EnqueueOnMain(() => ResetEpisode(true));
                	Thread.Sleep(netTickMs); // give main thread a tick
                	SendObs(ns);             // sends latest JPEG + reward/done flags

                	// Step loop: read two float32 (BE), set pending action, sleep, send obs
                	var buf = new byte[8];
                	while (_running && client.Connected)
                	{
                    	if (!ReadExact(ns, buf, 0, 8)) break;

                    	float steer = ReadFloatBE(buf, 0);
                    	float throttle = ReadFloatBE(buf, 4);
                    	_pendingSteer = Mathf.Clamp(steer, -1f, 1f);
                    	_pendingThrottle = Mathf.Clamp(throttle, 0f, 1f);
                    	_havePendingAction = true;

                    	Thread.Sleep(netTickMs); // let main thread step & capture
                    	SendObs(ns);

                    	if (_lastDone == 1 || _lastTruncated == 1)
                    	{
                        	// Wait for next 'R'
                        	int r = ns.ReadByte();
                        	while (r != 'R')
                        	{
                            	if (r < 0) throw new Exception("Client closed before next reset");
                            	r = ns.ReadByte();
                        	}
                        	EnqueueOnMain(() => ResetEpisode(true));
                        	Thread.Sleep(netTickMs);
                        	SendObs(ns);
                    	}
                	}
            	}
            	_clientConnected = false;
        	}
    	}
    	catch (ThreadInterruptedException) { /* exiting */ }
    	catch (SocketException se) { Debug.LogWarning("NetLoop socket: " + se.Message); }
    	catch (Exception e)       { Debug.LogWarning("NetLoop exception: " + e.Message); }
    	finally { _clientConnected = false; }
	}

    private void SendObs(NetworkStream ns)
    {
        byte[] jpg;
        lock (_jpegLock) { jpg = _latestJpeg ?? Array.Empty<byte>(); }
        int len = (jpg != null) ? jpg.Length : 0;

        // header length (big-endian)
        byte[] hdr = new byte[4];
        hdr[0] = (byte)((len >> 24) & 0xFF);
        hdr[1] = (byte)((len >> 16) & 0xFF);
        hdr[2] = (byte)((len >> 8) & 0xFF);
        hdr[3] = (byte)(len & 0xFF);
        ns.Write(hdr, 0, 4);

        if (len > 0) ns.Write(jpg, 0, len);

        // tail: reward (float32 big-endian) + done byte + truncated byte
        byte[] tail = new byte[6];
        WriteFloatBE(tail, 0, _lastReward);
        tail[4] = _lastDone;
        tail[5] = _lastTruncated;
        ns.Write(tail, 0, 6);
        ns.Flush();
    }

    private static bool ReadExact(NetworkStream ns, byte[] buf, int off, int len)
    {
        int read = 0;
        while (read < len)
        {
            int r = ns.Read(buf, off + read, len - read);
            if (r <= 0) return false;
            read += r;
        }
        return true;
    }

    private static float ReadFloatBE(byte[] buf, int offset)
    {
        byte[] tmp = new byte[4];
        Buffer.BlockCopy(buf, offset, tmp, 0, 4);
        if (BitConverter.IsLittleEndian) Array.Reverse(tmp);
        return BitConverter.ToSingle(tmp, 0);
    }

    private static void WriteFloatBE(byte[] dst, int offset, float v)
    {
        byte[] bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        Buffer.BlockCopy(bytes, 0, dst, offset, 4);
    }

    private void EnqueueOnMain(Action a)
    {
        lock (_mainQueue) _mainQueue.Enqueue(a);
    }

    // Optional: collision-based termination
    void OnCollisionEnter(Collision c)
    {
        // Uncomment to kill on any collision:
        // SharedEpisodeFlags.TriggerKill();
    }
}