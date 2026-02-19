using System.Reflection;

using NUnit.Framework;
using UnityEngine;

using VoxelEngine.Core.Rendering;

public class VoxelRaytracerFeatureEdgeBlendTests
{
    [Test]
    public void Create_AllocatesEdgeBlendMaterial_WhenShaderAssigned()
    {
        var feature = ScriptableObject.CreateInstance<VoxelRaytracerFeature>();
        try
        {
            feature.settings = new VoxelRaytracerSettings();
            var shader = Shader.Find("Voxel/EdgeBlend");
            Assert.NotNull(shader, "Edge blend shader not found.");
            feature.settings.edgeBlendShader = shader;

            feature.Create();

            var field = typeof(VoxelRaytracerFeature).GetField("_edgeBlendMaterial", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field, "Edge blend material field missing.");
            var material = field.GetValue(feature) as Material;
            Assert.NotNull(material, "Edge blend material was not created.");
            Assert.AreEqual(shader, material.shader, "Edge blend material uses unexpected shader.");
        }
        finally
        {
            var disposeMethod = typeof(VoxelRaytracerFeature).GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Instance);
            disposeMethod?.Invoke(feature, new object[] { true });
            Object.DestroyImmediate(feature);
        }
    }
}
