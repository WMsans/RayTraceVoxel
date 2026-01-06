using UnityEngine;

namespace VoxelEngine.Core.Data
{
    [CreateAssetMenu(fileName = "SolidVoxel", menuName = "Voxel/Voxel Definitions/Solid Voxel")]
    public class SolidVoxelDefinition : VoxelDefinition
    {
        public SolidVoxelDefinition()
        {
            renderType = VoxelRenderType.Solid;
        }
    }
}

