using UnityEngine;

namespace Sonoma.Core.Quadtree
{
    [System.Serializable]
    public struct QuadtreeBounds
    {
        public Vector2 Min;
        public Vector2 Max;
        public int QuadIndex;

        public Vector2 Centre => (Min + Max) * 0.5f;
        public float Size => Max.x - Min.x;

        public QuadtreeBounds[] Subdivide()
        {
            float mx = (Min.x + Max.x) * 0.5f;
            float my = (Min.y + Max.y) * 0.5f;

            return new[]
            {
                new QuadtreeBounds { Min = new Vector2(Min.x, my), Max = new Vector2(mx, Max.y), QuadIndex = QuadIndex }, // NW
                new QuadtreeBounds { Min = new Vector2(mx, my), Max = new Vector2(Max.x, Max.y), QuadIndex = QuadIndex }, // NE
                new QuadtreeBounds { Min = new Vector2(Min.x, Min.y), Max = new Vector2(mx, my), QuadIndex = QuadIndex }, // SW
                new QuadtreeBounds { Min = new Vector2(mx, Min.y), Max = new Vector2(Max.x, my), QuadIndex = QuadIndex }, // SE
            };
        }
    }
}
