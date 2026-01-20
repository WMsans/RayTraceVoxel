using UnityEngine;
using System.Collections.Generic;

namespace VoxelEngine.Core.Rendering
{
    public static class GrassMeshGenerator
    {
        /// <summary>
        /// Generates a simple cross-quad mesh for grass blades.
        /// Pivot is at (0,0,0).
        /// </summary>
        /// <param name="width">Width of the blade (total width of one quad).</param>
        /// <param name="height">Height of the blade.</param>
        /// <returns>A Mesh object ready for GPU instancing.</returns>
        public static Mesh GenerateBlade(float width = 0.5f, float height = 1.0f)
        {
            Mesh mesh = new Mesh();
            mesh.name = "GrassBlade_Cross";

            float halfWidth = width * 0.5f;

            // We create 2 intersecting quads (4 triangles, 8 vertices for distinct normals/UVs)
            // Quad 1: Aligned along X axis
            // Quad 2: Aligned along Z axis
            
            Vector3[] verts = new Vector3[8];
            Vector2[] uvs = new Vector2[8];
            int[] indices = new int[12]; // 2 quads * 2 tris * 3 indices

            // --- Quad 1 (X-Axis Plane) ---
            verts[0] = new Vector3(-halfWidth, 0, 0); // Bottom-Left
            verts[1] = new Vector3(halfWidth, 0, 0);  // Bottom-Right
            verts[2] = new Vector3(-halfWidth, height, 0); // Top-Left
            verts[3] = new Vector3(halfWidth, height, 0);  // Top-Right

            uvs[0] = new Vector2(0, 0);
            uvs[1] = new Vector2(1, 0);
            uvs[2] = new Vector2(0, 1);
            uvs[3] = new Vector2(1, 1);

            // Triangle 1
            indices[0] = 0; indices[1] = 2; indices[2] = 1;
            // Triangle 2
            indices[3] = 2; indices[4] = 3; indices[5] = 1;

            // --- Quad 2 (Z-Axis Plane) ---
            verts[4] = new Vector3(0, 0, -halfWidth); // Bottom-Left
            verts[5] = new Vector3(0, 0, halfWidth);  // Bottom-Right
            verts[6] = new Vector3(0, height, -halfWidth); // Top-Left
            verts[7] = new Vector3(0, height, halfWidth);  // Top-Right

            uvs[4] = new Vector2(0, 0);
            uvs[5] = new Vector2(1, 0);
            uvs[6] = new Vector2(0, 1);
            uvs[7] = new Vector2(1, 1);

            // Triangle 3
            indices[6] = 4; indices[7] = 6; indices[8] = 5;
            // Triangle 4
            indices[9] = 6; indices[10] = 7; indices[11] = 5;

            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = indices;
            
            // Recompute normals for lighting
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}