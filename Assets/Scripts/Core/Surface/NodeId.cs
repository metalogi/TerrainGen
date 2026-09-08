using System;

namespace Sonoma.Core.Surface
{
    // Edge numbering is South, East, North, West so that Opposite(e) == (e + 2) & 3.
    // ChunkMeshLayout's skirt edges use the same order and rely on it.
    public enum Edge : byte { South = 0, East = 1, North = 2, West = 3 }

    // Addresses any quadtree node without walking the tree.
    // Quad indexes into the root array returned by SurfaceMath.BuildRoots.
    // The node covers u in [X/Span, (X+1)/Span] and v in [Y/Span, (Y+1)/Span].
    public readonly struct NodeId : IEquatable<NodeId>
    {
        public readonly int Quad, Depth, X, Y;

        public NodeId(int quad, int depth, int x, int y)
        {
            Quad = quad; Depth = depth; X = x; Y = y;
        }

        // The deepest node this addressing scheme can represent.
        //
        // Span is `1 << Depth` in a signed int, so depth 31 evaluates to int.MinValue and
        // UMin/UMax/VMin/VMax return small negative numbers instead of failing -- the same
        // trap Parent guards against at the other end. 30 is the last depth that works.
        //
        // Not enforced in the constructor: NodeId is a value type built inside Burst jobs
        // and on every Child() call, and a branch there costs more than it buys. It is
        // enforced once, where the configuration is validated, in LodMath.Create.
        public const int MaxAddressableDepth = 30;

        public int  Span   => 1 << Depth;   // nodes per axis at this depth
        public bool IsRoot => Depth == 0;

        // Guarded because C# masks the shift count to 5 bits: a Depth of -1 makes Span
        // evaluate 1 << 31 == int.MinValue, and UMin/UMax/VMin/VMax then return small
        // negative values instead of failing. Test IsRoot (or use TryGetParent) first.
        // The message is a constant so this stays Burst-compilable.
        public NodeId Parent
        {
            get
            {
                if (Depth <= 0)
                    throw new InvalidOperationException(
                        "NodeId.Parent: node is a root (Depth 0); check IsRoot before ascending.");
                return new NodeId(Quad, Depth - 1, X >> 1, Y >> 1);
            }
        }

        // Non-throwing form, for tree walks that ascend until they run out of parents.
        public bool TryGetParent(out NodeId parent)
        {
            parent = Depth > 0 ? new NodeId(Quad, Depth - 1, X >> 1, Y >> 1) : default;
            return Depth > 0;
        }

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
