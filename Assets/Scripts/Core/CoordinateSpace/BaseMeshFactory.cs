using Unity.Mathematics;
using System;

namespace Sonoma.Core.CoordinateSpace
{
    public static class BaseMeshFactory
    {
        // ── Plane ────────────────────────────────────────────────────────────

        // Single quad on XZ plane centered at origin. Kept for DemoTerrainSpawner.
        public static BaseMeshQuad CreatePlaneQuad(float size)
        {
            float half = size * 0.5f;
            var up = new float3(0f, 1f, 0f);
            return new BaseMeshQuad
            {
                Type = SurfaceType.Plane,
                P00 = new double3(-half, 0.0, -half),
                P10 = new double3( half, 0.0, -half),
                P01 = new double3(-half, 0.0,  half),
                P11 = new double3( half, 0.0,  half),
                N00 = up, N10 = up, N01 = up, N11 = up,
            };
        }

        public static BaseMeshQuad[] CreatePlane(float size)
            => new[] { CreatePlaneQuad(size) };

        // ── UV Sphere ────────────────────────────────────────────────────────
        //
        // Tiles the sphere with (cols × rows) quads, Type = Sphere.
        // Each quad stores its lat/lon range; CoordinateTransform computes surface
        // positions analytically so all subdivision depths conform to the true sphere.
        // u ∈ [0,1] → longitude 0..2π.  v ∈ [0,1] → latitude -π/2..+π/2.
        // Polar rows produce degenerate edges (two corners coincide at pole).

        public static BaseMeshQuad[] CreateUVSphere(float radius, int cols, int rows)
        {
            var quads = new BaseMeshQuad[cols * rows];
            int idx   = 0;

            for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                quads[idx++] = new BaseMeshQuad
                {
                    Type         = SurfaceType.Sphere,
                    SphereRadius = radius,
                    Lat0 = -Math.PI * 0.5 + row       * (Math.PI / rows),
                    Lat1 = -Math.PI * 0.5 + (row + 1) * (Math.PI / rows),
                    Lon0 = col       * (2.0 * Math.PI / cols),
                    Lon1 = (col + 1) * (2.0 * Math.PI / cols),
                };
            }
            return quads;
        }

        // ── Cylinder ─────────────────────────────────────────────────────────
        //
        // Tiles the cylinder wall with (cols) quads, Type = Cylinder.
        // Axis along Z (horizontal). Cross-section in XY plane.
        // u ∈ [0,1] → angle 0..2π.  v ∈ [0,1] → length along Z.
        // Normals face inward for interior (O'Neill cylinder) viewing.

        public static BaseMeshQuad[] CreateCylinder(float radius, float length, int cols)
        {
            var quads = new BaseMeshQuad[cols];

            for (int col = 0; col < cols; col++)
            {
                quads[col] = new BaseMeshQuad
                {
                    Type      = SurfaceType.Cylinder,
                    CylRadius = radius,
                    Angle0    = col       * (2.0 * Math.PI / cols),
                    Angle1    = (col + 1) * (2.0 * Math.PI / cols),
                    CylZ0     = 0,
                    CylZ1     = length,
                };
            }
            return quads;
        }
    }
}
