using System;

namespace Sonoma.Core.Surface
{
    // Edge numbering is South, East, North, West so that Opposite(e) == (e + 2) & 3.
    // NOT interchangeable with Sonoma.Core.Quadtree.EdgeDirection, which is ordered
    // North, South, East, West. Never cast between the two.
    public enum Edge : byte { South = 0, East = 1, North = 2, West = 3 }

    // Addresses any quadtree node without walking the tree.
    // Quad indexes into the root array returned by Surface.BuildRoots.
    // The node covers u in [X/Span, (X+1)/Span] and v in [Y/Span, (Y+1)/Span].
    public readonly struct NodeId : IEquatable<NodeId>
    {
        public readonly int Quad, Depth, X, Y;

        public NodeId(int quad, int depth, int x, int y)
        {
            Quad = quad; Depth = depth; X = x; Y = y;
        }

        public int  Span   => 1 << Depth;   // nodes per axis at this depth
        public bool IsRoot => Depth == 0;

        public NodeId Parent => new NodeId(Quad, Depth - 1, X >> 1, Y >> 1);

        // i: bit 0 = +u, bit 1 = +v.  0 = SW, 1 = SE, 2 = NW, 3 = NE
        public NodeId Child(int i)
            => new NodeId(Quad, Depth + 1, (X << 1) | (i & 1), (Y << 1) | ((i >> 1) & 1));

        public double UMin => (double)X / Span;
        public double UMax => (double)(X + 1) / Span;
        public double VMin => (double)Y / Span;
        public double VMax => (double)(Y + 1) / Span;

        public bool Equals(NodeId o) => Quad == o.Quad && Depth == o.Depth && X == o.X && Y == o.Y;
        public override bool Equals(object o) => o is NodeId n && Equals(n);
        public override int GetHashCode() => HashCode.Combine(Quad, Depth, X, Y);
        public override string ToString() => $"Node(q{Quad} d{Depth} {X},{Y})";

        public static bool operator ==(NodeId a, NodeId b) =>  a.Equals(b);
        public static bool operator !=(NodeId a, NodeId b) => !a.Equals(b);
    }

    // Result of a neighbour query. ArrivalEdge is the edge of Node's root quad through
    // which we entered; following it back from Node returns to the original node, which
    // is what makes cross-quad traversal round-trippable.
    public struct NeighbourResult
    {
        public NodeId Node;
        public Edge   ArrivalEdge;
        public bool   Exists;
    }
}
