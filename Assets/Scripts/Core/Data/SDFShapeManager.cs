using System.Collections.Generic;
using UnityEngine;
using VInspector;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VoxelEngine.Core.Data
{
    /// <summary>
    /// Manages the registry of SDF Shapes and packs them into a single Texture3D Atlas.
    /// </summary>
    [CreateAssetMenu(fileName = "SDFShapeManager", menuName = "Voxel/SDF Shape Manager")]
    public class SDFShapeManager : ScriptableObjectSingleton<SDFShapeManager>
    {
        [Header("Registry")]
        public List<SDFShapeDefinition> shapes = new List<SDFShapeDefinition>();

        [Header("GPU Data")]
        [Tooltip("The generated atlas containing all shapes stacked along the Z axis.")]
        public Texture3D sdfAtlas;
        
        [Header("Settings")]
        [Tooltip("Resolution for each individual shape (e.g. 32 means 32x32x32).")]
        public int targetResolution = 32;
        public TextureFormat textureFormat = TextureFormat.RFloat;

        /// <summary>
        /// Gets the index of a shape in the list. This corresponds to its Z-block in the atlas.
        /// </summary>
        public int GetShapeIndex(SDFShapeDefinition shape)
        {
            return shapes.IndexOf(shape);
        }

        // Context menu allows you to run this from the Inspector
        [ButtonAttribute]
        public void Initialize()
        {
            if (shapes == null || shapes.Count == 0) 
            {
                Debug.LogWarning("[SDFShapeManager] No shapes to pack.");
                return;
            }

            int numShapes = shapes.Count;
            int totalDepth = numShapes * targetResolution;

            // 1. Create Texture (In Memory)
            // We create a new one to ensure clean data
            Texture3D newAtlas = new Texture3D(targetResolution, targetResolution, totalDepth, textureFormat, false);
            newAtlas.wrapMode = TextureWrapMode.Clamp;
            newAtlas.filterMode = FilterMode.Trilinear;
            newAtlas.name = "GlobalSDFAtlas";

            // 2. Pack Data
            Color[] atlasPixels = new Color[targetResolution * targetResolution * totalDepth];

            for (int i = 0; i < numShapes; i++)
            {
                var def = shapes[i];
                if (def == null || !def.IsValid) continue;

                if (def.sdfTexture.width != targetResolution)
                {
                    Debug.LogWarning($"[SDFShapeManager] Shape '{def.name}' resolution ({def.sdfTexture.width}) != target ({targetResolution}). Skipping.");
                    continue;
                }

                // Copy pixels
                Color[] srcPixels = def.sdfTexture.GetPixels();
                int pixelCount = srcPixels.Length;
                int startOffset = i * pixelCount;
                
                if (startOffset + pixelCount <= atlasPixels.Length)
                {
                    System.Array.Copy(srcPixels, 0, atlasPixels, startOffset, pixelCount);
                }
            }

            newAtlas.SetPixels(atlasPixels);
            newAtlas.Apply();

            // 3. Assign & Save (Editor Only)
            sdfAtlas = newAtlas;

#if UNITY_EDITOR
            // This allows the Texture to persist in the project view and clears "Type Mismatch" errors
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

            Debug.Log($"[SDFShapeManager] Packed {numShapes} shapes into Atlas ({targetResolution}x{targetResolution}x{totalDepth}). Saved to asset.");
        }
    }
}