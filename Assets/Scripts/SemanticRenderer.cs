using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using System.Collections.Generic;
using System;

/// <summary>
/// Generates semantic segmentation masks alongside RGB captures for training ML models.
/// Renders scene with replacement shaders to create labeled segmentation masks.
/// </summary>
public class SemanticRenderer : MonoBehaviour
{
    [Header("Cameras")]
    [Tooltip("Main camera for RGB capture")]
    public Camera rgbCamera;
    
    [Tooltip("Camera for semantic segmentation (can be same as RGB camera)")]
    public Camera semanticCamera;
    
    [Header("Render Textures")]
    public RenderTexture rgbRenderTexture;
    public RenderTexture semanticRenderTexture;
    
    [Header("Output Settings")]
    public string outputFolder = "Assets/SemanticCaptures";
    public string rgbPrefix = "frame";
    public bool captureEnabled = true;
    public int captureInterval = 5; // Capture every N frames
    
    [Header("Semantic Segmentation Settings")]
    [Tooltip("Shader for semantic rendering")]
    public Shader semanticShader;
    
    [Header("Layer Assignments")]
    [Tooltip("Assign GameObjects to these layers in Unity")]
    public LayerMask roadLayer;
    public LayerMask laneLineLayer;
    public LayerMask sidewalkLayer;
    public LayerMask vehicleLayer;
    public LayerMask buildingLayer;
    
    // Semantic class colors (matching Python config)
    private readonly Color backgroundColor = Color.black;           // Class 0
    private readonly Color roadColor = new Color(0.5f, 0.5f, 0.5f); // Class 1 (128,128,128)
    private readonly Color laneLineColor = Color.yellow;            // Class 2 (255,255,0)
    private readonly Color sidewalkColor = new Color(0.25f, 0.25f, 0.25f); // Class 3 (64,64,64)
    
    private Texture2D rgbTexture;
    private Texture2D semanticTexture;
    private int frameCounter = 0;
    private int captureCounter = 0;
    
    // Replacement shader material cache
    private Dictionary<int, Material> layerMaterials;
    private Material semanticMaterial;
    
    void Start()
    {
        InitializeCapture();
        CreateSemanticShader();
        SetupLayerMaterials();
    }
    
    void InitializeCapture()
    {
        // Create output directory
        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
            Debug.Log($"Created output folder: {outputFolder}");
        }
        
        // Initialize render textures if not assigned
        if (rgbRenderTexture == null)
        {
            rgbRenderTexture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            rgbRenderTexture.Create();
        }
        
        if (semanticRenderTexture == null)
        {
            semanticRenderTexture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            semanticRenderTexture.Create();
        }
        
        // Initialize textures for reading pixels
        rgbTexture = new Texture2D(rgbRenderTexture.width, rgbRenderTexture.height, 
                                   TextureFormat.RGB24, false);
        semanticTexture = new Texture2D(semanticRenderTexture.width, semanticRenderTexture.height,
                                        TextureFormat.RGB24, false);
        
        // Assign render textures to cameras
        if (rgbCamera != null)
            rgbCamera.targetTexture = rgbRenderTexture;
            
        if (semanticCamera != null)
            semanticCamera.targetTexture = semanticRenderTexture;
        
        Debug.Log("Semantic Renderer initialized");
    }
    
    void CreateSemanticShader()
    {
        // Create a simple unlit shader for semantic rendering if not provided
        if (semanticShader == null)
        {
            string shaderCode = @"
                Shader ""Custom/SemanticSegmentation""
                {
                    Properties
                    {
                        _Color (""Semantic Color"", Color) = (1,1,1,1)
                    }
                    SubShader
                    {
                        Tags { ""RenderType""=""Opaque"" }
                        LOD 100
                        
                        Pass
                        {
                            CGPROGRAM
                            #pragma vertex vert
                            #pragma fragment frag
                            #include ""UnityCG.cginc""
                            
                            struct appdata
                            {
                                float4 vertex : POSITION;
                            };
                            
                            struct v2f
                            {
                                float4 vertex : SV_POSITION;
                            };
                            
                            fixed4 _Color;
                            
                            v2f vert (appdata v)
                            {
                                v2f o;
                                o.vertex = UnityObjectToClipPos(v.vertex);
                                return o;
                            }
                            
                            fixed4 frag (v2f i) : SV_Target
                            {
                                return _Color;
                            }
                            ENDCG
                        }
                    }
                }";
            
            semanticShader = Shader.Find("Unlit/Color");
            if (semanticShader == null)
            {
                Debug.LogWarning("Could not find semantic shader, using Unlit/Color");
            }
        }
        
        semanticMaterial = new Material(semanticShader);
    }
    
    void SetupLayerMaterials()
    {
        layerMaterials = new Dictionary<int, Material>();
        
        // Create materials for each semantic class
        Material roadMat = new Material(semanticShader);
        roadMat.SetColor("_Color", roadColor);
        layerMaterials[LayerMask.NameToLayer("Road")] = roadMat;
        
        Material laneLineMat = new Material(semanticShader);
        laneLineMat.SetColor("_Color", laneLineColor);
        layerMaterials[LayerMask.NameToLayer("LaneLines")] = laneLineMat;
        
        Material sidewalkMat = new Material(semanticShader);
        sidewalkMat.SetColor("_Color", sidewalkColor);
        layerMaterials[LayerMask.NameToLayer("Sidewalk")] = sidewalkMat;
        
        Debug.Log($"Set up {layerMaterials.Count} layer materials for semantic rendering");
    }
    
    void Update()
    {
        if (captureEnabled && frameCounter % captureInterval == 0)
        {
            CaptureFrame();
        }
        frameCounter++;
    }
    
    void CaptureFrame()
    {
        StartCoroutine(CaptureFrameCoroutine());
    }
    
    System.Collections.IEnumerator CaptureFrameCoroutine()
    {
        // Wait for end of frame to ensure rendering is complete
        yield return new WaitForEndOfFrame();
        
        // Capture RGB
        if (rgbCamera != null)
        {
            CaptureRGB();
        }
        
        // Capture Semantic
        if (semanticCamera != null)
        {
            CaptureSemantic();
        }
        
        captureCounter++;
        
        if (captureCounter % 100 == 0)
        {
            Debug.Log($"Captured {captureCounter} frame pairs");
        }
    }
    
    void CaptureRGB()
    {
        // Render and read RGB
        rgbCamera.Render();
        RenderTexture.active = rgbRenderTexture;
        rgbTexture.ReadPixels(new Rect(0, 0, rgbRenderTexture.width, rgbRenderTexture.height), 0, 0);
        rgbTexture.Apply();
        
        // Save RGB image
        byte[] rgbBytes = rgbTexture.EncodeToJPG(90);
        string rgbPath = Path.Combine(outputFolder, $"{rgbPrefix}_{captureCounter:D4}_rgb.jpg");
        File.WriteAllBytes(rgbPath, rgbBytes);
        
        RenderTexture.active = null;
    }
    
    void CaptureSemantic()
    {
        // Store original camera settings
        CameraClearFlags originalClearFlags = semanticCamera.clearFlags;
        Color originalBackground = semanticCamera.backgroundColor;
        
        // Set camera for semantic rendering
        semanticCamera.clearFlags = CameraClearFlags.SolidColor;
        semanticCamera.backgroundColor = backgroundColor;
        
        // Use replacement shader rendering
        if (semanticShader != null)
        {
            semanticCamera.RenderWithShader(semanticShader, "RenderType");
        }
        else
        {
            // Fallback: render with layer-specific materials
            RenderSemanticWithLayers();
        }
        
        // Read semantic texture
        RenderTexture.active = semanticRenderTexture;
        semanticTexture.ReadPixels(new Rect(0, 0, semanticRenderTexture.width, 
                                           semanticRenderTexture.height), 0, 0);
        semanticTexture.Apply();
        
        // Convert to grayscale for smaller file size