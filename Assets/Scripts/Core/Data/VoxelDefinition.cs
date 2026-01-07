using Unity.Mathematics;
using UnityEngine;

namespace VoxelEngine.Core.Data
{
    /// <summary>
    /// Defines the texture and material properties for a voxel.
    /// This is now part of VoxelDefinition.
    /// </summary>
    [System.Serializable]
    public struct BlockTextures
    {
        [Header("Side Textures")]
        public Texture2D Albedo;
        public Texture2D Normal;
        [UnityEngine.Serialization.FormerlySerializedAs("Smoothness")]
        public Texture2D Roughness;
        [Range(0f, 1f)]
        public float Metallic; // Metallic value for side faces
        public Texture2D AmbientOcclusion;
        
        [Header("Top Textures (Optional)")]
        [Tooltip("Assign all four top textures to use them. Otherwise, the side textures will be used for the top face.")]
        public Texture2D TopAlbedo;
        public Texture2D TopNormal;
        [UnityEngine.Serialization.FormerlySerializedAs("TopSmoothness")]
        public Texture2D TopRoughness;
        [Range(0f, 1f)]
        public float TopMetallic; // Metallic value for top faces
        public Texture2D TopAmbientOcclusion;
        
        /// <summary>
        /// Determines if dedicated top textures should be used for this block.
        /// </summary>
        /// <returns>True if all top texture fields are assigned, false otherwise.</returns>
        public bool HasSeparateTopTextures() => TopAlbedo != null && TopNormal != null && TopRoughness != null && TopAmbientOcclusion != null;
    }


    /// <summary>
    /// A ScriptableObject base class to define the properties and behavior of a voxel type.
    /// </summary>
    public abstract class VoxelDefinition : ScriptableObject
    {
        public enum VoxelRenderType
        {
            Air = 255,
            Solid = 1,
            Liquid = 2,
            Transparent = 3,
            Hollowed = 4,
        }
        [Tooltip("The unique ID for this voxel type (0-255). This must match its position in the VoxelManager's list.")]
        public byte id;

        [Tooltip("A human-readable name for this voxel.")]
        public string voxelName;

        [Header("Basic Properties")]

        [Tooltip("Is this voxel considered 'solid' for marching cubes and physics?")]
        public VoxelRenderType renderType;

        [Header("Texture & Material Properties")]
        [Tooltip("Defines the textures and material properties for this block.")]
        public BlockTextures blockTextures;
    }
}