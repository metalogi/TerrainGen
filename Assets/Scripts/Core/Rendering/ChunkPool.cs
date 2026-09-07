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

        // Every chunk this pool ever made, acquired or not. _free alone is not enough to
        // dispose from: at teardown the live chunks are all checked out, so a Dispose that
        // walks only the free stack frees nothing it created.
        readonly List<TerrainChunk> _all = new List<TerrainChunk>();

        public int Created   => _all.Count;
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

            _all.Add(chunk);
            return chunk;
        }

        // Teardown. Destroys every mesh the pool created, whether or not it was released
        // first -- _free alone would free nothing, since at teardown the live chunks are all
        // checked out. The GameObjects go too: in play mode the scene would take them, but
        // an EditMode test has no scene teardown to rely on.
        public void Dispose()
        {
            foreach (var chunk in _all)
            {
                if (chunk == null) continue;
                Destroy(chunk.Mesh);
                Destroy(chunk.gameObject);
            }
            _all.Clear();
            _free.Clear();
        }

        // Object.Destroy defers to the end of the frame, which never comes outside play
        // mode -- it throws there instead. EditMode tests drive this pool directly, so the
        // teardown path has to work in both.
        static void Destroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else                       Object.DestroyImmediate(o);
        }
    }
}
