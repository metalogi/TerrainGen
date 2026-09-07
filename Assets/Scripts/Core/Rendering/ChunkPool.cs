using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Sonoma.Core.Generation;

namespace Sonoma.Core.Rendering
{
    // Recycles chunk GameObjects and their Meshes.
    //
    // Meshes are never destroyed while the game is running: allocating and freeing a mesh per
    // chunk is one of the few things in this pipeline that has to happen on the main thread
    // and cannot be budgeted away. A released chunk keeps its mesh and gets overwritten in
    // place by the next upload.
    public class ChunkPool
    {
        readonly Transform      _parent;
        readonly Material       _material;
        readonly int            _resolution;
        readonly Stack<TerrainChunk> _free = new Stack<TerrainChunk>();

        int _created;

        public int Created   => _created;
        public int Available => _free.Count;

        public ChunkPool(Transform parent, Material material, int resolution)
        {
            _parent     = parent;
            _material   = material;
            _resolution = resolution;
        }

        public TerrainChunk Acquire()
        {
            TerrainChunk chunk = _free.Count > 0 ? _free.Pop() : Create();
            chunk.gameObject.SetActive(true);
            return chunk;
        }

        public void Release(TerrainChunk chunk)
        {
            if (chunk == null) return;
            chunk.gameObject.SetActive(false);
            _free.Push(chunk);
        }

        TerrainChunk Create()
        {
            var go = new GameObject("Chunk");
            go.transform.SetParent(_parent, false);

            var chunk = go.AddComponent<TerrainChunk>();
            chunk.SetMaterial(_material);

            var mesh = new Mesh { name = "ChunkMesh" };
            mesh.MarkDynamic();
            mesh.indexFormat = ChunkMeshBuffers.IndexFormatFor(_resolution);
            chunk.SetMesh(mesh);

            _created++;
            return chunk;
        }

        // Play-mode teardown only. Destroys the meshes the pool owns; the GameObjects go with
        // the scene.
        public void Dispose()
        {
            foreach (var chunk in _free)
            {
                if (chunk == null) continue;
                if (chunk.Mesh != null) Object.Destroy(chunk.Mesh);
            }
            _free.Clear();
        }
    }
}
