using System.Collections.Generic;
using UnityEngine;
using VInspector;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VoxelEngine.Core.Data
{
    [CreateAssetMenu(fileName = "SDFShapeManager", menuName = "Voxel/SDF Shape Manager")]
    public class SDFShapeManager : ScriptableObjectSingleton<SDFShapeManager>
    {
        [Header("Registry")]
        public List<SDFShapeDefinition> shapes = new List<SDFShapeDefinition>();

        [Header("GPU Data")]
        public Texture3D sdfAtlas;
        
        [Header("Settings")]
        public int targetResolution = 32;
        
        // Use R8 (Unsigned 0..1) for best compatibility and size
        public TextureFormat textureFormat = TextureFormat.R8; 

        public int GetShapeIndex(SDFShapeDefinition shape) => shapes.IndexOf(shape);

        [ButtonAttribute]
        public void Initialize()
        {
            if (shapes == null || shapes.Count == 0) return;

            int numShapes = shapes.Count;
            int totalDepth = numShapes * targetResolution;

            Texture3D newAtlas = new Texture3D(targetResolution, targetResolution, totalDepth, textureFormat, false);
            newAtlas.wrapMode = TextureWrapMode.Clamp;
            newAtlas.filterMode = FilterMode.Trilinear;
            newAtlas.name = "GlobalSDFAtlas";

            Color[] atlasPixels = new Color[targetResolution * targetResolution * totalDepth];

            for (int i = 0; i < numShapes; i++)
            {
                var def = shapes[i];
                if (def == null || !def.IsValid) continue;

                Color[] srcPixels = def.sdfTexture.GetPixels();
                int pixelCount = srcPixels.Length;
                int startOffset = i * pixelCount;
                
                if (startOffset + pixelCount <= atlasPixels.Length)
                {
                    // MANUAL ENCODING: Map [-1, 1] to [0, 1]
                    // If we just copy R8, negative values (inside shape) get clamped to 0.
                    for(int p = 0; p < pixelCount; p++)
                    {
                        // Assume source RFloat is in range [-1, 1] (typical for normalized SDF)
                        float signedDist = srcPixels[p].r;
                        float packed = signedDist * 0.5f + 0.5f; 
                        atlasPixels[startOffset + p] = new Color(packed, 0, 0, 0);
                    }
                }
            }

            newAtlas.SetPixels(atlasPixels);
            newAtlas.Apply();
            sdfAtlas = newAtlas;

#if UNITY_EDITOR
            if (AssetDatabase.Contains(this))
            {
                string path = AssetDatabase.GetAssetPath(this);
                
                // Load all sub-assets at this path
                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var asset in assets)
                {
                    // Clean up old atlas textures if they exist
                    if (asset is Texture3D && asset.name == "GlobalSDFAtlas")
                    {
                        DestroyImmediate(asset, true);
                    }
                }

                // Add the new atlas as a sub-asset of the Manager
                AssetDatabase.AddObjectToAsset(sdfAtlas, this);
                AssetDatabase.SaveAssets();
                
                // Force Editor to refresh the inspector
                EditorUtility.SetDirty(this);
                EditorGUIUtility.PingObject(sdfAtlas); 
            }
#endif
            Debug.Log($"[SDFShapeManager] Packed {numShapes} shapes (R8 Encoded).");
        }
    }
}