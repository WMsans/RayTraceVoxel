using UnityEngine;
using System.Collections.Generic;

namespace VoxelEngine.Core.Rendering
{
    public static class GrassMeshGenerator
    {
        /// <summary>
        /// Generates a cross-triangle mesh for grass blades.
        /// Supports segments for smooth bending.
        /// </summary>
        /// <param name="width">Width of the blade base.</param>
        /// <param name="height">Height of the blade.</param>
        /// <param name="segments">Number of vertical segments (default 1 for backward compatibility).</param>
        public static Mesh GenerateBlade(float width = 0.5f, float height = 1.0f, int segments = 1)
        {
            Mesh mesh = new Mesh();
            mesh.name = "GrassBlade_Procedural";

            List<Vector3> verts = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> indices = new List<int>();

            // Generate two planes (0 and 90 degrees)
            for (int p = 0; p < 2; p++)
            {
                float angle = p * 90.0f * Mathf.Deg2Rad;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                // Build segments
                for (int i = 0; i < segments; i++)
                {
                    float t0 = (float)i / segments;
                    float t1 = (float)(i + 1) / segments;

                    float w0 = width * (1.0f - t0); // Taper width
                    float w1 = width * (1.0f - t1);

                    // Special case: Top segment goes to a point (w1 = 0)
                    bool isTip = (i == segments - 1);

                    float y0 = t0 * height;
                    float y1 = t1 * height;

                    // Vertices for this segment (Quad or Tri at tip)
                    // We build a Quad: BL, BR, TL, TR
                    // If tip, TL and TR are the same point.

                    int baseIndex = verts.Count;

                    // Bottom Left
                    verts.Add(new Vector3(-w0 * 0.5f * cos, y0, -w0 * 0.5f * sin));
                    uvs.Add(new Vector2(0, t0));

                    // Bottom Right
                    verts.Add(new Vector3(w0 * 0.5f * cos, y0, w0 * 0.5f * sin));
                    uvs.Add(new Vector2(1, t0));

                    if (isTip)
                    {
                        // Tip Vertex (Center)
                        verts.Add(new Vector3(0, y1, 0));
                        uvs.Add(new Vector2(0.5f, 1));

                        // Triangle indices
                        indices.Add(baseIndex);     // BL
                        indices.Add(baseIndex + 2); // Tip
                        indices.Add(baseIndex + 1); // BR
                    }
                    else
                    {
                        // Top Left
                        verts.Add(new Vector3(-w1 * 0.5f * cos, y1, -w1 * 0.5f * sin));
                        uvs.Add(new Vector2(0, t1));

                        // Top Right
                        verts.Add(new Vector3(w1 * 0.5f * cos, y1, w1 * 0.5f * sin));
                        uvs.Add(new Vector2(1, t1));

                        // Quad Indices (2 Tris)
                        // Tri 1
                        indices.Add(baseIndex);     // BL
                        indices.Add(baseIndex + 2); // TL
                        indices.Add(baseIndex + 1); // BR

                        // Tri 2
                        indices.Add(baseIndex + 1); // BR
                        indices.Add(baseIndex + 2); // TL
                        indices.Add(baseIndex + 3); // TR
                    }
                }
            }

            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0);
            
            // Normals: Pointing mostly UP (0,1,0) gives better lighting for grass than face normals
            Vector3[] normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++)
                normals[i] = Vector3.up;
            
            mesh.normals = normals;
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}