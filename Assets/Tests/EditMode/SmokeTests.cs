using NUnit.Framework;
using Sonoma.Core.CoordinateSpace;

namespace Sonoma.Tests
{
    public class SmokeTests
    {
        // Placeholder proving the test assembly compiles and can see Sonoma.Core.
        // M1 replaces this file with the surface adjacency and round-trip tests.
        [Test]
        public void PlaneQuadHasPlaneSurfaceType()
        {
            var quad = BaseMeshFactory.CreatePlaneQuad(1000f);
            Assert.AreEqual(SurfaceType.Plane, quad.Type);
        }
    }
}
