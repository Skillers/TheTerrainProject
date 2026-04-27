using UnityEngine;

namespace Terrain.Data
{
    // Plain data, built off the main thread. Turned into a UnityEngine.Mesh in Phase G on the main thread.
    public struct MeshData
    {
        public Vector3[] Vertices;
        public int[]     Triangles;
        public Vector2[] Uvs;
        public Color32[] Colors;
        public Vector3[] Normals;

        public static MeshData Allocate(int vertexCount, int triangleCount)
        {
            return new MeshData
            {
                Vertices  = new Vector3[vertexCount],
                Triangles = new int[triangleCount * 3],
                Uvs       = new Vector2[vertexCount],
                Colors    = new Color32[vertexCount],
                Normals   = new Vector3[vertexCount],
            };
        }
    }
}
