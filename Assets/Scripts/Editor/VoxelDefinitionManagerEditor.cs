using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Core.Data;

[CustomEditor(typeof(VoxelDefinitionManager))]
public class VoxelDefinitionManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        VoxelDefinitionManager manager = (VoxelDefinitionManager)target;

        if (GUILayout.Button("Pack Atlas"))
        {
            PackAtlas(manager);
        }
    }

    private void PackAtlas(VoxelDefinitionManager manager)
    {
        // 1. Setup Directories
        string folderPath = "Assets/Resources/Textures/Packed";
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        // 2. Prepare Lists
        List<Color[]> albedoPixels = new List<Color[]>();
        List<Color[]> normalPixels = new List<Color[]>();
        List<Color[]> maskPixels = new List<Color[]>();

        Dictionary<Texture2D, int> albedoMap = new Dictionary<Texture2D, int>();
        Dictionary<Texture2D, int> normalMap = new Dictionary<Texture2D, int>();
        Dictionary<(Texture2D, Texture2D), int> maskMap = new Dictionary<(Texture2D, Texture2D), int>();

        int res = manager.textureResolution;
        if (res <= 0) res = 256;

        // 3. Add Default Textures (Index 0)
        albedoPixels.Add(CreateSolidColorPixels(Color.white, res));
        normalPixels.Add(CreateSolidColorPixels(new Color(0.5f, 0.5f, 1.0f), res));
        maskPixels.Add(CreateSolidColorPixels(new Color(0f, 1f, 0f, 0.5f), res));

        // 4. Process Definitions
        List<VoxelTypeGPU> gpuDataList = new List<VoxelTypeGPU>();
        
        // Ensure definitions list is not null
        if (manager.definitions == null) manager.definitions = new List<VoxelDefinition>();

        foreach (var def in manager.definitions)
        {
            VoxelTypeGPU data = new VoxelTypeGPU();
            
            if (def != null)
            {
                data.renderType = (uint)def.renderType;
                data.sideMetallic = def.blockTextures.Metallic;

                // Side
                data.sideAlbedoIndex = (uint)GetOrAddTexture(def.blockTextures.Albedo, albedoMap, albedoPixels, res, false);
                data.sideNormalIndex = (uint)GetOrAddTexture(def.blockTextures.Normal, normalMap, normalPixels, res, true);
                data.sideMaskIndex = (uint)GetOrAddMask(def.blockTextures.AmbientOcclusion, def.blockTextures.Roughness, maskMap, maskPixels, res);

                // Top
                if (def.blockTextures.HasSeparateTopTextures())
                {
                    data.topMetallic = def.blockTextures.TopMetallic;
                    data.topAlbedoIndex = (uint)GetOrAddTexture(def.blockTextures.TopAlbedo, albedoMap, albedoPixels, res, false);
                    data.topNormalIndex = (uint)GetOrAddTexture(def.blockTextures.TopNormal, normalMap, normalPixels, res, true);
                    data.topMaskIndex = (uint)GetOrAddMask(def.blockTextures.TopAmbientOcclusion, def.blockTextures.TopRoughness, maskMap, maskPixels, res);
                }
                else
                {
                    data.topMetallic = data.sideMetallic;
                    data.topAlbedoIndex = data.sideAlbedoIndex;
                    data.topNormalIndex = data.sideNormalIndex;
                    data.topMaskIndex = data.sideMaskIndex;
                }
            }
            
            gpuDataList.Add(data);
        }

        // 5. Create Arrays
        Texture2DArray albedoArray = CreateArray(albedoPixels, res, false);
        Texture2DArray normalArray = CreateArray(normalPixels, res, true);
        Texture2DArray maskArray = CreateArray(maskPixels, res, true);

        // 6. Save Assets
        string albedoPath = folderPath + "/VoxelAlbedoArray.asset";
        string normalPath = folderPath + "/VoxelNormalArray.asset";
        string maskPath = folderPath + "/VoxelMaskArray.asset";

        AssetDatabase.CreateAsset(albedoArray, albedoPath);
        AssetDatabase.CreateAsset(normalArray, normalPath);
        AssetDatabase.CreateAsset(maskArray, maskPath);
        
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Reload to get references
        albedoArray = AssetDatabase.LoadAssetAtPath<Texture2DArray>(albedoPath);
        normalArray = AssetDatabase.LoadAssetAtPath<Texture2DArray>(normalPath);
        maskArray = AssetDatabase.LoadAssetAtPath<Texture2DArray>(maskPath);

        // 7. Update Manager
        manager.SetPackedData(gpuDataList, albedoArray, normalArray, maskArray);
        EditorUtility.SetDirty(manager);
        AssetDatabase.SaveAssets();

        Debug.Log($"Packed Atlas! Definitions: {manager.definitions.Count}. Textures: {albedoPixels.Count} Albedo, {normalPixels.Count} Normal, {maskPixels.Count} Mask.");
    }

    private int GetOrAddTexture(Texture2D tex, Dictionary<Texture2D, int> map, List<Color[]> list, int res, bool isNormal)
    {
        if (tex == null) return 0; // Use default
        if (map.TryGetValue(tex, out int index)) return index;

        // Process
        Color[] pixels = GetResizedPixels(tex, res, isNormal);
        list.Add(pixels);
        int newIndex = list.Count - 1;
        map[tex] = newIndex;
        return newIndex;
    }

    private int GetOrAddMask(Texture2D ao, Texture2D roughness, Dictionary<(Texture2D, Texture2D), int> map, List<Color[]> list, int res)
    {
        if (ao == null && roughness == null) return 0; // Default
        if (map.TryGetValue((ao, roughness), out int index)) return index;

        // Composite Mask
        Color[] aoPixels = (ao != null) ? GetResizedPixels(ao, res, false) : null;
        Color[] roPixels = (roughness != null) ? GetResizedPixels(roughness, res, false) : null;
        
        Color[] maskResult = new Color[res * res];
        
        for (int i = 0; i < maskResult.Length; i++)
        {
            float aoVal = (aoPixels != null) ? aoPixels[i].g : 1.0f; // Default AO is 1 (White)
            float roVal = (roPixels != null) ? roPixels[i].r : 0.5f; // Default Roughness 0.5
            
            // Pack: R(Unused/Metallic), G(AO), B(Roughness), A(Unused)
            maskResult[i] = new Color(0, aoVal, roVal, 1);
        }

        list.Add(maskResult);
        int newIndex = list.Count - 1;
        map[(ao, roughness)] = newIndex;
        return newIndex;
    }

    private Color[] GetResizedPixels(Texture2D source, int res, bool isNormal)
    {
        RenderTexture tempRT = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.ARGB32, isNormal ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        
        // Blit to resize
        Graphics.Blit(source, tempRT);
        
        // Readback
        Texture2D tempTex = new Texture2D(res, res, TextureFormat.ARGB32, false);
        RenderTexture.active = tempRT;
        tempTex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
        tempTex.Apply();
        
        Color[] pixels = tempTex.GetPixels();
        
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(tempRT);
        DestroyImmediate(tempTex); // Cleanup temp texture
        
        return pixels;
    }

    private Color[] CreateSolidColorPixels(Color c, int res)
    {
        Color[] p = new Color[res * res];
        for (int i = 0; i < p.Length; i++) p[i] = c;
        return p;
    }

    private Texture2DArray CreateArray(List<Color[]> pixelData, int res, bool linear)
    {
        if (pixelData.Count == 0) return null;

        Texture2DArray array = new Texture2DArray(res, res, pixelData.Count, TextureFormat.RGBA32, true, linear);
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
