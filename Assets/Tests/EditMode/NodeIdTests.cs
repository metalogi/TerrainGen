using System.Collections.Generic;
using NUnit.Framework;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // NodeId is the key type for M2's scheduler dictionaries, so its equality,
    // hashing and parent/child arithmetic all have to be exact.
    public class NodeIdTests
    {
        [Test]
        public void ChildParentRoundTrip()
        {
            for (int depth = 0; depth <= 8; depth++)
            {
                int span = 1 << depth;
                // Sample a spread rather than the full grid: depth 8 is 65536 nodes per quad.
                int step = span > 8 ? span / 8 : 1;

                for (int x = 0; x < span; x += step)
                for (int y = 0; y < span; y += step)
                {
                    var n = new NodeId(3, depth, x, y);
                    for (int i = 0; i < 4; i++)
                    {
                        var child = n.Child(i);
                        Assert.AreEqual(depth + 1, child.Depth, $"{n}.Child({i}) has wrong depth");
                        Assert.AreEqual(n, child.Parent, $"{n}.Child({i}).Parent != {n}");
                        Assert.AreEqual(n.Quad, child.Quad, $"{n}.Child({i}) changed quad");
                    }
                }
            }
        }

        [Test]
        public void ChildUvRangesTileParent()
        {
            const double eps = 1e-15;
            var n = new NodeId(0, 3, 5, 6);

            double mid(double a, double b) => (a + b) * 0.5;
            double uMid = mid(n.UMin, n.UMax);
            double vMid = mid(n.VMin, n.VMax);

            // Child index bit 0 = +u, bit 1 = +v.
            var expected = new (double u0, double u1, double v0, double v1)[]
            {
                (n.UMin, uMid,   n.VMin, vMid  ),  // 0 SW
                (uMid,   n.UMax, n.VMin, vMid  ),  // 1 SE
                (n.UMin, uMid,   vMid,   n.VMax),  // 2 NW
                (uMid,   n.UMax, vMid,   n.VMax),  // 3 NE
            };

            for (int i = 0; i < 4; i++)
            {
                var c = n.Child(i);
                Assert.AreEqual(expected[i].u0, c.UMin, eps, $"child {i} UMin");
                Assert.AreEqual(expected[i].u1, c.UMax, eps, $"child {i} UMax");
                Assert.AreEqual(expected[i].v0, c.VMin, eps, $"child {i} VMin");
                Assert.AreEqual(expected[i].v1, c.VMax, eps, $"child {i} VMax");
            }

            // The four children exactly cover the parent with no gap or overlap.
            Assert.AreEqual(n.UMin, n.Child(0).UMin, eps);
            Assert.AreEqual(n.UMax, n.Child(3).UMax, eps);
            Assert.AreEqual(n.Child(0).UMax, n.Child(1).UMin, eps, "u gap between SW and SE");
            Assert.AreEqual(n.Child(0).VMax, n.Child(2).VMin, eps, "v gap between SW and NW");
        }

        [Test]
        public void EqualityAndHashing()
        {
            var a = new NodeId(2, 5, 11, 7);
            var b = new NodeId(2, 5, 11, 7);
            var c = new NodeId(2, 5, 11, 8);

            Assert.AreEqual(a, b);
            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode(), "equal NodeIds must hash equally");

            Assert.AreNotEqual(a, c);
            Assert.IsTrue(a != c);

            // Differing in exactly one component must not collide with the others.
            var set = new HashSet<NodeId>
            {
                new NodeId(0, 0, 0, 0),
                new NodeId(1, 0, 0, 0),
                new NodeId(0, 1, 0, 0),
                new NodeId(0, 0, 1, 0),
                new NodeId(0, 0, 0, 1),
            };
            Assert.AreEqual(5, set.Count, "NodeIds differing in one component collapsed in a HashSet");

            set.Add(new NodeId(0, 0, 0, 0));
            Assert.AreEqual(5, set.Count, "duplicate NodeId was not de-duplicated");
        }
    }
}
