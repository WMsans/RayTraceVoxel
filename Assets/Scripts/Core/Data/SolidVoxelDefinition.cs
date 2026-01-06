using UnityEngine;

[CreateAssetMenu(fileName = "SolidVoxel", menuName = "Voxel/Voxel Definitions/Solid Voxel")]
public class SolidVoxelDefinition : VoxelDefinition
{
    public SolidVoxelDefinition()
    {
        renderType = VoxelRenderType.Solid;
    }
}
