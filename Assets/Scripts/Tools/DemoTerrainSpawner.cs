using UnityEngine;
using UnityEngine.Rendering;
using Sonoma.Systems.Configuration;
using Sonoma.Core.Generation;
using Sonoma.Core.Rendering;
using Sonoma.Core.CoordinateSpace;
using Unity.Mathematics;

public class DemoTerrainSpawner : MonoBehaviour
{
    public TerrainSettings Settings;
    public Material Material;
    public float BaseSize = 1000f;
    public int OverrideResolution = 0;

    void Start()
    {
        var s = Settings;
        if (s == null)
            s = ScriptableObject.CreateInstance<TerrainSettings>();

        int res = OverrideResolution > 0 ? OverrideResolution : s.ChunkResolution;

        BaseMeshQuad quad = BaseMeshFactory.CreatePlaneQuad(BaseSize);
        var hm = HeightmapGenerator.Generate(res, Vector2.zero, Vector2.one, quad, s.BaseFrequency, s.Octaves);

        Mesh mesh = BuildMeshFromHeightmap(hm, res, quad, s.HeightScale);

        var go = new GameObject("DemoTerrainChunk");
        var chunk = go.AddComponent<TerrainChunk>();
        if (Material != null) chunk.Initialize(Material);
        chunk.ApplyMesh(mesh);
        go.transform.position = Vector3.zero;
    }

    Mesh BuildMeshFromHeightmap(float[,] hm, int res, BaseMeshQuad quad, float heightScale)
    {
        int vertCount = res * res;
        Vector3[] verts = new Vector3[vertCount];
        Vector3[] normals = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];
        int[] tris = new int[(res - 1) * (res - 1) * 6];

        // positions
        for (int y = 0; y < res; y++)
        {
            float v = Mathf.Lerp(0f, 1f, y / (float)(res - 1));
            for (int x = 0; x < res; x++)
            {
                float u = Mathf.Lerp(0f, 1f, x / (float)(res - 1));
                int i = y * res + x;

                double3 world = CoordinateTransform.ToWorldPosition(u, v, hm[x, y] * heightScale, quad);
                double3 local = world - WorldOriginSystem.WorldOrigin;
                verts[i] = new Vector3((float)local.x, (float)local.y, (float)local.z);
                uvs[i] = new Vector2(u, v);
            }
        }

        // normals via central differences on positions
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                int i = y * res + x;
                Vector3 pos = verts[i];

                Vector3 left = verts[(y * res) + Mathf.Max(0, x - 1)];
                Vector3 right = verts[(y * res) + Mathf.Min(res - 1, x + 1)];
                Vector3 down = verts[(Mathf.Max(0, y - 1) * res) + x];
                Vector3 up = verts[(Mathf.Min(res - 1, y + 1) * res) + x];

                Vector3 dx = right - left;
                Vector3 dy = up - down;
                Vector3 n = Vector3.Cross(dy, dx);
                if (n.sqrMagnitude <= 1e-6f) n = Vector3.up;
                normals[i] = n.normalized;
            }
        }

        // indices
        int ti = 0;
        for (int y = 0; y < res - 1; y++)
        {
            for (int x = 0; x < res - 1; x++)
            {
                int i00 = y * res + x;
                int i10 = i00 + 1;
                int i01 = i00 + res;
                int i11 = i01 + 1;

                tris[ti++] = i00; tris[ti++] = i11; tris[ti++] = i10;
                tris[ti++] = i00; tris[ti++] = i01; tris[ti++] = i11;
            }
        }

        var mesh = new Mesh();
        mesh.indexFormat = (vertCount > 65535) ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }
}
