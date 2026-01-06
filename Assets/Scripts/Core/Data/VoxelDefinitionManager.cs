using System.Collections.Generic;
using UnityEngine;
using System.Runtime.InteropServices;

namespace VoxelEngine.Core.Data
{
    /// <summary>
    /// Manages the registry of VoxelDefinitions and packs them into GPU-compatible formats
    /// (TextureArrays and ComputeBuffers).
    /// </summary>
    public class VoxelDefinitionManager : MonoBehaviour
    {
        public static VoxelDefinitionManager Instance { get; private set; }

        [Header("Configuration")]
        [Tooltip("The resolution for all textures in the arrays. Textures will be resized to this.")]
        public int textureResolution = 256;
        
        [Tooltip("List of Voxel Definitions. The order determines the ID (index 0 is usually Air/Empty).")]
        public List<VoxelDefinition> definitions = new List<VoxelDefinition>();

        [Header("GPU Data")]
        public Texture2DArray albedoTextureArray;
        public Texture2DArray normalTextureArray;
        public Texture2DArray maskTextureArray; // Packed: G=AO, A=Smoothness
        
        private GraphicsBuffer _voxelMaterialBuffer;
        public GraphicsBuffer VoxelMaterialBuffer => _voxelMaterialBuffer;

        private void Awake()
        {
            if (Instance != null && Instance != this) Destroy(this);
            else Instance = this;
        }

        private void Start()
        {
            Initialize();
        }

        private void OnDestroy()
        {
            if (_voxelMaterialBuffer != null) _voxelMaterialBuffer.Release();
        }

        public void Initialize()
        {
            if (definitions == null || definitions.Count == 0)
            {
                Debug.LogWarning("VoxelDefinitionManager: No definitions assigned.");
                return;
            }

            // Clean up old data
            if (_voxelMaterialBuffer != null) _voxelMaterialBuffer.Release();
            // TextureArrays are assets or managed by GC, but if we created them effectively "new", we let old ones go.

            // We will process definitions and build lists of pixel data
            List<Color[]> albedoPixels = new List<Color[]>();
            List<Color[]> normalPixels = new List<Color[]>();
            List<Color[]> maskPixels = new List<Color[]>();

            // Caches to avoid duplicates
            Dictionary<Texture2D, int> albedoMap = new Dictionary<Texture2D, int>();
            Dictionary<Texture2D, int> normalMap = new Dictionary<Texture2D, int>();
            // Mask Key: (AO Texture, Smoothness Texture)
            Dictionary<(Texture2D, Texture2D), int> maskMap = new Dictionary<(Texture2D, Texture2D), int>();

            // --- 1. Add Default Textures (Index 0) ---
            // Albedo: White
            albedoPixels.Add(CreateSolidColorPixels(Color.white));
            
            // Normal: Flat Blue (0.5, 0.5, 1.0)
            normalPixels.Add(CreateSolidColorPixels(new Color(0.5f, 0.5f, 1.0f)));

            // Mask: G=1 (AO full), A=0.5 (Smoothness mid), R=0, B=0
            maskPixels.Add(CreateSolidColorPixels(new Color(0f, 1f, 0f, 0.5f)));


            // --- 2. Process Definitions ---
            VoxelTypeGPU[] gpuData = new VoxelTypeGPU[definitions.Count];

            for (int i = 0; i < definitions.Count; i++)
            {
                VoxelDefinition def = definitions[i];
                VoxelTypeGPU data = new VoxelTypeGPU();
                
                if (def == null)
                {
                    gpuData[i] = data; // Empty/Default
                    continue;
                }

                data.renderType = (uint)def.renderType;
                data.sideMetallic = def.blockTextures.Metallic;
                
                // Side Textures
                data.sideAlbedoIndex = (uint)GetOrAddTexture(def.blockTextures.Albedo, albedoMap, albedoPixels, false);
                data.sideNormalIndex = (uint)GetOrAddTexture(def.blockTextures.Normal, normalMap, normalPixels, true); // true for normal map
                data.sideMaskIndex = (uint)GetOrAddMask(def.blockTextures.AmbientOcclusion, def.blockTextures.Smoothness, maskMap, maskPixels);

                // Top Textures
                if (def.blockTextures.HasSeparateTopTextures())
                {
                    data.topMetallic = def.blockTextures.TopMetallic;
                    data.topAlbedoIndex = (uint)GetOrAddTexture(def.blockTextures.TopAlbedo, albedoMap, albedoPixels, false);
                    data.topNormalIndex = (uint)GetOrAddTexture(def.blockTextures.TopNormal, normalMap, normalPixels, true);
                    data.topMaskIndex = (uint)GetOrAddMask(def.blockTextures.TopAmbientOcclusion, def.blockTextures.TopSmoothness, maskMap, maskPixels);
                }
                else
                {
                    // Re-use side
                    data.topMetallic = data.sideMetallic;
                    data.topAlbedoIndex = data.sideAlbedoIndex;
                    data.topNormalIndex = data.sideNormalIndex;
                    data.topMaskIndex = data.sideMaskIndex;
                }

                gpuData[i] = data;
            }

            // --- 3. Create Arrays ---
            albedoTextureArray = CreateArray(albedoPixels, false);
            normalTextureArray = CreateArray(normalPixels, true); // Linear for normals usually
            maskTextureArray = CreateArray(maskPixels, true); // Linear for masks

            // --- 4. Upload Buffer ---
            _voxelMaterialBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gpuData.Length, Marshal.SizeOf<VoxelTypeGPU>());
            _voxelMaterialBuffer.SetData(gpuData);

            Debug.Log($"VoxelDefinitionManager: Initialized. {definitions.Count} Definitions. AlbedoArray: {albedoPixels.Count}, NormalArray: {normalPixels.Count}, MaskArray: {maskPixels.Count}");
        }

        private int GetOrAddTexture(Texture2D tex, Dictionary<Texture2D, int> map, List<Color[]> list, bool isNormal)
        {
            if (tex == null) return 0; // Use default
            if (map.TryGetValue(tex, out int index)) return index;

            // Process
            Color[] pixels = GetResizedPixels(tex, isNormal);
            list.Add(pixels);
            int newIndex = list.Count - 1;
            map[tex] = newIndex;
            return newIndex;
        }

        private int GetOrAddMask(Texture2D ao, Texture2D smoothness, Dictionary<(Texture2D, Texture2D), int> map, List<Color[]> list)
        {
            if (ao == null && smoothness == null) return 0; // Default
            if (map.TryGetValue((ao, smoothness), out int index)) return index;

            // Composite Mask
            Color[] aoPixels = (ao != null) ? GetResizedPixels(ao, false) : null;
            Color[] smPixels = (smoothness != null) ? GetResizedPixels(smoothness, false) : null;
            
            Color[] maskResult = new Color[textureResolution * textureResolution];
            
            for (int i = 0; i < maskResult.Length; i++)
            {
                float aoVal = (aoPixels != null) ? aoPixels[i].g : 1.0f; // Default AO is 1 (White)
                float smVal = (smPixels != null) ? smPixels[i].r : 0.5f; // Default Smoothness 0.5? Or use alpha channel of source?
                // Assuming Smoothness texture is Greyscale (R=G=B).
                // NOTE: If Smoothness is packed differently in source, adjust here.
                
                // Pack: R(Unused/Metallic), G(AO), B(Unused), A(Smoothness)
                maskResult[i] = new Color(0, aoVal, 0, smVal);
            }

            list.Add(maskResult);
            int newIndex = list.Count - 1;
            map[(ao, smoothness)] = newIndex;
            return newIndex;
        }

        private Color[] GetResizedPixels(Texture2D source, bool isNormal)
        {
            RenderTexture tempRT = RenderTexture.GetTemporary(textureResolution, textureResolution, 0, RenderTextureFormat.ARGB32, isNormal ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            
            // Blit to resize
            Graphics.Blit(source, tempRT);
            
            // Readback
            Texture2D tempTex = new Texture2D(textureResolution, textureResolution, TextureFormat.ARGB32, false);
            RenderTexture.active = tempRT;
            tempTex.ReadPixels(new Rect(0, 0, textureResolution, textureResolution), 0, 0);
            tempTex.Apply();
            
            Color[] pixels = tempTex.GetPixels();
            
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(tempRT);
            DestroyImmediate(tempTex); // Cleanup temp texture
            
            return pixels;
        }

        private Color[] CreateSolidColorPixels(Color c)
        {
            Color[] p = new Color[textureResolution * textureResolution];
            for (int i = 0; i < p.Length; i++) p[i] = c;
            return p;
        }

        private Texture2DArray CreateArray(List<Color[]> pixelData, bool linear)
        {
            if (pixelData.Count == 0) return null;

            Texture2DArray array = new Texture2DArray(textureResolution, textureResolution, pixelData.Count, TextureFormat.RGBA32, true, linear);
            array.filterMode = FilterMode.Bilinear;
            array.wrapMode = TextureWrapMode.Repeat; // Important for voxels

            for (int i = 0; i < pixelData.Count; i++)
            {
                array.SetPixels(pixelData[i], i);
            }
            array.Apply();
            return array;
        }
    }
}

