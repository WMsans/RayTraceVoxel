using UnityEngine;
using System.Collections.Generic;

namespace VoxelEngine.Core.Rendering
{
    public static class GrassMeshGenerator
    {
        /// <summary>
        /// Generates a simple cross-triangle mesh for grass blades.
        /// Pivot is at (0,0,0). Top is sharp (tapered).
        /// </summary>
        /// <param name="width">Width of the blade base.</param>
        /// <param name="height">Height of the blade.</param>
        /// <returns>A Mesh object ready for GPU instancing (2 triangles).</returns>
        public static Mesh GenerateBlade(float width = 0.5f, float height = 1.0f)
        {
            Mesh mesh = new Mesh();
            mesh.name = "GrassBlade_Sharp";

            float halfWidth = width * 0.5f;

            // We create 2 intersecting triangles (2 tris, 6 vertices for distinct normals/UVs)
            // Plane 1: Aligned along X axis
            // Plane 2: Aligned along Z axis
            
            Vector3[] verts = new Vector3[6];
            Vector2[] uvs = new Vector2[6];
            int[] indices = new int[6]; // 2 triangles * 3 indices

            // --- Triangle 1 (X-Axis Plane) ---
            verts[0] = new Vector3(-halfWidth, 0, 0); // Bottom-Left
            verts[1] = new Vector3(halfWidth, 0, 0);  // Bottom-Right
            verts[2] = new Vector3(0, height, 0);     // Top-Center (Sharp Tip)

            uvs[0] = new Vector2(0, 0);
            uvs[1] = new Vector2(1, 0);
            uvs[2] = new Vector2(0.5f, 1); // Tip UV centered

            // Triangle 1 Indices
            indices[0] = 0; indices[1] = 2; indices[2] = 1;

            // --- Triangle 2 (Z-Axis Plane) ---
            verts[3] = new Vector3(0, 0, -halfWidth); // Bottom-Left
            verts[4] = new Vector3(0, 0, halfWidth);  // Bottom-Right
            verts[5] = new Vector3(0, height, 0);     // Top-Center (Sharp Tip)

            uvs[3] = new Vector2(0, 0);
            uvs[4] = new Vector2(1, 0);
            uvs[5] = new Vector2(0.5f, 1); // Tip UV centered

            // Triangle 2 Indices
            indices[3] = 3; indices[4] = 5; indices[5] = 4;

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