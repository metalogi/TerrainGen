using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Sonoma.Core.Rendering
{
    // One rendered chunk. Pooled: ChunkPool creates and reuses these, so Awake/OnDestroy are
    // not the lifecycle that matters -- Acquire/Release are.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TerrainChunk : MonoBehaviour
    {
        // AllActive: only enabled chunks.
        public static readonly HashSet<TerrainChunk> AllActive = new HashSet<TerrainChunk>();
        // AllChunks: every chunk including hidden ones, so a parent held inactive during
        // subdivision is not missed by an origin rebase.
        public static readonly HashSet<TerrainChunk> AllChunks = new HashSet<TerrainChunk>();

        // The chunk's node centre on the base surface, in absolute world space.
        //
        // Mesh vertices are stored relative to this, and the transform is recomputed from it
        // on every rebase rather than shifted by a delta. Repeated `position -= shift` in
        // float accumulates error over a long session; recomputing from a double anchor does
        // not. M4 stress-tests this over a thousand rebases.
        public double3 Anchor;

        MeshFilter   _mf;
        MeshRenderer _mr;

        public Mesh Mesh => _mf != null ? _mf.sharedMesh : null;

        void Awake()
        {
            _mf = GetComponent<MeshFilter>();
            _mr = GetComponent<MeshRenderer>();
            AllChunks.Add(this);
        }

        void OnDestroy()  { AllChunks.Remove(this); AllActive.Remove(this); }
        void OnEnable()   { AllActive.Add(this); }
        void OnDisable()  { AllActive.Remove(this); }

        public void SetMaterial(Material mat)
        {
            if (_mr == null) _mr = GetComponent<MeshRenderer>();
            _mr.sharedMaterial = mat;
        }

        public void SetMesh(Mesh mesh)
        {
            if (_mf == null) _mf = GetComponent<MeshFilter>();
            _mf.sharedMesh = mesh;
        }

        public void Rebase(double3 origin)
        {
            double3 local = Anchor - origin;
            transform.position = new Vector3((float)local.x, (float)local.y, (float)local.z);
        }
    }
}
