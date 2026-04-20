using System.Collections.Generic;
using UnityEngine;

namespace Sonoma.Core.Rendering
{
    public class TerrainChunk : MonoBehaviour
    {
        // AllActive: only enabled chunks — used for rendering/selection logic
        public static HashSet<TerrainChunk> AllActive = new HashSet<TerrainChunk>();
        // AllChunks: every chunk including hidden ones — used by WorldOriginSystem so
        // parent chunks that are SetActive(false) during subdivision don't miss a rebase.
        public static HashSet<TerrainChunk> AllChunks = new HashSet<TerrainChunk>();

        MeshFilter mf;
        MeshRenderer mr;

        void Awake()  { AllChunks.Add(this); }
        void OnDestroy() { AllChunks.Remove(this); }

        void OnEnable()
        {
            AllActive.Add(this);
            mf = gameObject.GetComponent<MeshFilter>();
            mr = gameObject.GetComponent<MeshRenderer>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
        }

        void OnDisable()
        {
            AllActive.Remove(this);
        }

        public void Initialize(Material mat)
        {
            if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }

        public void ApplyMesh(Mesh mesh)
        {
            if (mf == null) mf = gameObject.GetComponent<MeshFilter>();
            mf.sharedMesh = mesh;
        }

        public void Dispose()
        {
            if (mf != null && mf.sharedMesh != null)
            {
                Destroy(mf.sharedMesh);
                mf.sharedMesh = null;
            }
            Destroy(gameObject);
        }
    }
}
