using System.Collections.Generic;
using UnityEngine;
using VInspector;
using System.Linq;

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
        public ComputeShader meshSdfCompute; // Reference to MeshSDF.compute
        
        [Header("Settings")]
        public int targetResolution = 32;
        
        // Use R8 (Unsigned 0..1) for best compatibility and size
        public TextureFormat textureFormat = TextureFormat.R8; 

        public int GetShapeIndex(SDFShapeDefinition shape) => shapes.IndexOf(shape);

        // Struct matching MeshSDF.compute
        private struct Triangle 
        {
            public Vector3 v0;
            public Vector3 v1;
            public Vector3 v2;
            public int materialID;
        }

        [ButtonAttribute]
        public void Initialize()
        {
            if (shapes == null || shapes.Count == 0) return;

            // 1. Bake any missing textures from meshes
            foreach (var def in shapes)
            {
                if (def != null && def.sourceMesh != null && def.sdfTexture == null)
                {
                    BakeMeshToTexture(def);
                }
            }

            // 2. Pack Atlas
            PackAtlas();
        }

        private void BakeMeshToTexture(SDFShapeDefinition def)
        {
            if (meshSdfCompute == null)
            {
                Debug.LogError("MeshSDF Compute Shader not assigned in SDFShapeManager!");
                return;
            }
            if (!def.sourceMesh.isReadable)
            {
                Debug.LogError($"Mesh '{def.sourceMesh.name}' is not readable. Please enable Read/Write in import settings.");
                return;
            }

            int res = targetResolution; // Force match target resolution for atlas compatibility
            def.resolution = res;

            // --- 1. Prepare Data ---
            Vector3[] vertices = def.sourceMesh.vertices;
            int[] indices = def.sourceMesh.triangles;
            int triCount = indices.Length / 3;

            Triangle[] triangleData = new Triangle[triCount];
            for (int i = 0; i < triCount; i++)
            {
                triangleData[i] = new Triangle
                {
                    v0 = vertices[indices[i * 3 + 0]],
                    v1 = vertices[indices[i * 3 + 1]],
                    v2 = vertices[indices[i * 3 + 2]],
                    materialID = 0 // Default
                };
            }

            // --- 2. Calculate Cubic Bounds ---
            Bounds meshBounds = def.sourceMesh.bounds;
            float maxDim = Mathf.Max(meshBounds.size.x, Mathf.Max(meshBounds.size.y, meshBounds.size.z));
            float paddedSize = maxDim + (def.padding * 2.0f);
            Vector3 boundsSize = Vector3.one * paddedSize;
            Vector3 boundsMin = meshBounds.center - (boundsSize * 0.5f);

            // --- 3. Setup Compute ---
            int kernel = meshSdfCompute.FindKernel("CSMain");
            
            ComputeBuffer triBuffer = new ComputeBuffer(triCount, 40); // 3*float3(12) + int(4) = 40 bytes
            triBuffer.SetData(triangleData);

            int voxelCount = res * res * res;
            ComputeBuffer sdfBuffer = new ComputeBuffer(voxelCount, sizeof(float));
            ComputeBuffer materialBuffer = new ComputeBuffer(voxelCount, sizeof(int));

            meshSdfCompute.SetBuffer(kernel, "_Triangles", triBuffer);
            meshSdfCompute.SetBuffer(kernel, "_SDFBuffer", sdfBuffer);
            meshSdfCompute.SetBuffer(kernel, "_DenseMaterialBuffer", materialBuffer);
            
            meshSdfCompute.SetInt("_TriangleCount", triCount);
            meshSdfCompute.SetInt("_Resolution", res);
            meshSdfCompute.SetVector("_BoundsMin", boundsMin);
            meshSdfCompute.SetVector("_BoundsSize", boundsSize);

            int threads = Mathf.CeilToInt(res / 8.0f);
            meshSdfCompute.Dispatch(kernel, threads, threads, threads);

            // --- 4. Readback & Create Texture ---
            float[] sdfValues = new float[voxelCount];
            sdfBuffer.GetData(sdfValues);

            triBuffer.Release();
            sdfBuffer.Release();
            materialBuffer.Release();

            Texture3D tex = new Texture3D(res, res, res, TextureFormat.RFloat, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.name = $"{def.name}_SDF";

            Color[] pixels = new Color[voxelCount];
            for (int i = 0; i < voxelCount; i++)
            {
                // Shader returns distance in Voxel Units. 
                // We normalize it to the Bounds Size so it fits the standard [-1, 1] range expected by the Atlas packer.
                // Distance 1.0 in result = 1.0 / Resolution of the full box.
                float normalizedDist = sdfValues[i] / (float)res;
                
                pixels[i] = new Color(normalizedDist, 0, 0, 1);
            }
            tex.SetPixels(pixels);
            tex.Apply();

            def.sdfTexture = tex;

#if UNITY_EDITOR
            // Save the generated texture as an asset so it persists
            string defPath = AssetDatabase.GetAssetPath(def);
            if (!string.IsNullOrEmpty(defPath))
            {
                AssetDatabase.AddObjectToAsset(tex, def);
                EditorUtility.SetDirty(def);
                AssetDatabase.SaveAssets();
            }
#endif
            Debug.Log($"[SDFShapeManager] Baked mesh '{def.sourceMesh.name}' to SDF Texture ({res}^3).");
        }

        private void PackAtlas()
        {
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

                // Ensure resolution match (simple safeguard, usually assumes match)
                if (def.sdfTexture.width != targetResolution)
                {
                    Debug.LogWarning($"Shape {def.name} resolution ({def.sdfTexture.width}) differs from Target ({targetResolution}). Atlas mapping may be incorrect.");
                }

                Color[] srcPixels = def.sdfTexture.GetPixels();
                int pixelCount = srcPixels.Length;
                // Correct offset logic for flat array
                int startOffset = i * (targetResolution * targetResolution * targetResolution);
                
                if (startOffset + pixelCount <= atlasPixels.Length)
                {
                    // MANUAL ENCODING: Map [-1, 1] to [0, 1]
                    for(int p = 0; p < pixelCount; p++)
                    {
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
                
                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var asset in assets)
                {
                    if (asset is Texture3D && asset.name == "GlobalSDFAtlas")
                    {
                        DestroyImmediate(asset, true);
                    }
                }

                AssetDatabase.AddObjectToAsset(sdfAtlas, this);
                AssetDatabase.SaveAssets();
                EditorUtility.SetDirty(this);
                EditorGUIUtility.PingObject(sdfAtlas); 
            }
#endif
            Debug.Log($"[SDFShapeManager] Packed {numShapes} shapes (R8 Encoded).");
        }
    }
}