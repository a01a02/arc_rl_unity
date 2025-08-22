using UnityEngine;

/// <summary>
/// Simple ego-centric local map stored on the car for telemetry/debugging.
/// Not provided to the policy (keeps the system purely passive).
/// - Tracks the car's trajectory since episode reset in a small occupancy texture.
/// - Optionally displays the texture on a MeshRenderer (e.g., a small quad on the hood/UI).
/// </summary>
public class LocalMap : MonoBehaviour
{
    [Header("Map Settings")]
    public int size = 128;                // pixels (square)
    public float metersPerPixel = 0.1f;   // world meters per pixel
    public int drawRadius = 1;            // pixels to thicken the trail
    public Color trailColor = Color.white;
    public Color backgroundColor = Color.black;
    public int applyEveryNFrames = 3;     // apply texture less often to save CPU

    [Header("Optional Display")]
    public MeshRenderer mapDisplay;       // assign a quad's renderer if you want to visualize
    public string textureProperty = "_MainTex";

    private Texture2D _tex;
    private Vector3 _originPos;
    private int _frameCounter = 0;
    private Color[] _bgRow;

    void Awake()
    {
        _tex = new Texture2D(size, size, TextureFormat.RGB24, false);
        _tex.filterMode = FilterMode.Point;
        _bgRow = new Color[size];
        for (int i = 0; i < size; i++) _bgRow[i] = backgroundColor;
        Clear();
        if (mapDisplay != null)
        {
            var mat = mapDisplay.material;
            mat.SetTexture(textureProperty, _tex);
        }
    }

    public void EpisodeReset()
    {
        _originPos = transform.position;
        Clear();
    }

    public void Clear()
    {
        for (int y = 0; y < size; y++)
            _tex.SetPixels(0, y, size, 1, _bgRow);
        _tex.Apply(false, false);
    }

    void FixedUpdate()
    {
        // position relative to origin set at episode reset
        Vector3 d = transform.position - _originPos;
        // project to XZ plane (Unity convention: X horizontal, Z forward)
        float px = d.x / metersPerPixel + size * 0.5f;
        float py = d.z / metersPerPixel + size * 0.5f;

        DrawDot(Mathf.RoundToInt(px), Mathf.RoundToInt(py), drawRadius, trailColor);

        _frameCounter++;
        if (_frameCounter % applyEveryNFrames == 0)
            _tex.Apply(false, false);
    }

    private void DrawDot(int cx, int cy, int radius, Color c)
    {
        int x0 = Mathf.Max(0, cx - radius);
        int x1 = Mathf.Min(size - 1, cx + radius);
        int y0 = Mathf.Max(0, cy - radius);
        int y1 = Mathf.Min(size - 1, cy + radius);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                _tex.SetPixel(x, y, c);
            }
        }
    }
}