using UnityEngine;

namespace VoxelEngine.Core.Data
{
    [CreateAssetMenu(fileName = "NewSDFShape", menuName = "Voxel/SDF Shape Definition")]
    public class SDFShapeDefinition : ScriptableObject
    {
        [Tooltip("The Mesh to bake (for reference/editor use).")]
        public Mesh sourceMesh;

        [Tooltip("The baked 3D Texture containing Signed Distance Field data.")]
        public Texture3D sdfTexture;

        [Tooltip("Padding added during baking to prevent clipping.")]
        public float padding = 0.1f;
        
        [Tooltip("The resolution of the 3D texture (e.g., 32, 64).")]
        public int resolution = 32;

        public bool IsValid => sdfTexture != null;
    }
}