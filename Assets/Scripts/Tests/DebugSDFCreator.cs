using UnityEngine;
using UnityEditor;
using VoxelEngine.Core.Data;

public class DebugSDFCreator
{
    [MenuItem("Voxel/Debug/Create Test Sphere SDF")]
    public static void CreateTestSphere()
    {
        int resolution = 32;
        Texture3D tex = new Texture3D(resolution, resolution, resolution, TextureFormat.RFloat, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;

        Color[] cols = new Color[resolution * resolution * resolution];
        float radius = 0.4f; // 0.4 of the 0..1 UV space
        Vector3 center = new Vector3(0.5f, 0.5f, 0.5f);

        for (int z = 0; z < resolution; z++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    // Normalized UV coordinates (0 to 1)
                    Vector3 uv = new Vector3(x, y, z) / (float)resolution;
                    
                    // Distance to center - radius
                    float dist = Vector3.Distance(uv, center) - radius;
                    
                    // Store distance in Red channel
                    cols[x + y * resolution + z * resolution * resolution] = new Color(dist, 0, 0, 0);
                }
            }
        }

        tex.SetPixels(cols);
        tex.Apply();

        // Save Texture
        string texPath = "Assets/Resources/TestSphere_SDF.asset";
        AssetDatabase.CreateAsset(tex, texPath);

        // Create Definition
        SDFShapeDefinition def = ScriptableObject.CreateInstance<SDFShapeDefinition>();
        def.sdfTexture = tex;
        def.resolution = resolution;
        
        string defPath = "Assets/Resources/TestSphere_Def.asset";
        AssetDatabase.CreateAsset(def, defPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Created Test SDF at {defPath}");
    }
}