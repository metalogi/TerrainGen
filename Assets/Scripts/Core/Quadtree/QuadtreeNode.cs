using System;
using UnityEngine;
using Sonoma.Core.Rendering;

namespace Sonoma.Core.Quadtree
{
    public enum NodeState { Inactive, Generating, Active, Subdivided }

    public class QuadtreeNode
    {
        public QuadtreeBounds Bounds;
        public int Depth;
        public QuadtreeNode Parent;
        public QuadtreeNode[] Children; // null when leaf
        public NodeState State = NodeState.Inactive;
        public TerrainChunk Chunk;

        public bool IsLeaf => Children == null || Children.Length == 0;

        public void Subdivide()
        {
            if (!IsLeaf) return;
            var childBounds = Bounds.Subdivide();
            Children = new QuadtreeNode[4];
            for (int i = 0; i < 4; i++)
            {
                Children[i] = new QuadtreeNode
                {
                    Bounds = childBounds[i],
                    Depth = Depth + 1,
                    Parent = this,
                    State = NodeState.Inactive
                };
            }
            State = NodeState.Subdivided;
        }

        public void Collapse()
        {
            if (IsLeaf) return;
            Children = null;
            State = NodeState.Active;
        }
    }
}
