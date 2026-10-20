using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Quadtree
{
    // The space LOD selection and the morph measure distance in: the world with every terrain
    // height scaled by LodVerticalScale, s. The geometry is drawn at full height; only the
    // distances that pick a chunk's depth and its morph factor see the flatter world.
    //
    // Why. The LOD relief allowance (LodMath.MaxHalfRelief) is a budget on slope: a node's
    // bounding sphere is widened by half its relief, and a vertex can be a whole sphere diameter
    // further out than the distance its node was selected at, so a node whose height range is
    // large against its size breaks the boundary condition. Mountains are exactly that, and
    // HeightScale multiplies every slope. At 5x the stress scenes' M5b relief, the roughest
    // measured nodes overrun the allowance by 1.4-1.6x and the relief tests' margins by 4-5x;
    // SplitFactor alone would need about 10 on Earth, roughly 37,000 chunks. In the flattened
    // world a terrain with s x 5 the relief spends the allowance, and wants the chunks, that the
    // terrain with 1x did -- every LOD figure the tests pin carries over at s x HeightScale.
    //
    // Why it closes the boundaries. The proof behind MaxMorphStartFraction and MaxHalfRelief uses
    // one fact about geometry: a vertex lies inside its own node's bounding sphere. Here both are
    // flattened by the same map -- the shader moves the vertex, LodMath.NodeDistance the sphere
    // -- so the fact holds in the flattened world with the relief scaled by s, and the proof goes
    // through unchanged. Morph targets and positions stay in the true world, consistent across
    // chunks as before; only k is decided here, and k is what has to agree.
    //
    // The map. A point moves along the surface's own normal, by -Flatten x (clamp(a, -E, E) - A),
    // where a is its altitude above the base surface, E the Envelope and A the Anchor:
    //
    //   inside the envelope -- every vertex, a camera among the terrain -- a goes to
    //       A + s (a - A), so altitudes, and height differences, shrink by s;
    //   above it -- a camera in orbit, or near a cylinder's axis -- a shifts by a constant and
    //       is not scaled, so orbit stays orbit rather than coming s times closer.
    //
    // It is 1-Lipschitz in altitude, so the vertical part of a distance never grows. The Anchor is
    // the envelope's side nearest the centre of curvature (-E on a sphere, +E inside a cylinder,
    // 0 on a plane), so the map only ever moves points towards that centre, which only ever
    // shortens the across-the-surface part too. So a flattened distance is never longer than
    // the true one: the LOD this picks is never coarser than true 3D distance would pick at the
    // same SplitFactor. Where height differences dominate -- looking down into a deep valley --
    // it is finer than needed, which costs chunks, never detail.
    //
    // Stored as Flatten = 1 - s, so default(LodVertical) is the identity and a configuration
    // that never sets it -- SampleScene, every test that builds a LodMath -- runs the old
    // arithmetic to the bit (C5's convention, as the biome gains are stored).
    //
    // No UnityEngine, so it runs in the out-of-Editor harness with LodMath.
    public struct LodVertical
    {
        public double Flatten;    // 1 - LodVerticalScale; 0 is the identity
        public double Envelope;   // a bound on |height| anywhere, metres
        public double Anchor;     // the height the map leaves where it is

        public double Scale      => 1.0 - Flatten;
        public bool   IsIdentity => Flatten == 0.0;

        // The envelope should be a bound, not an estimate -- TerrainHeightFunction.MaxAbsHeight.
        // A vertex outside it is still flattened consistently with its node's sphere, so nothing
        // cracks; only the never-coarser property is lost, for a camera between the bound and a
        // peak above it. A loose bound costs the other way: a camera between the real peaks and
        // the bound is still treated as among the terrain and gets finer LOD than it needs.
        public static LodVertical Create(in SurfaceDef surface, float verticalScale, double envelope)
        {
            if (!(verticalScale > 0f) || verticalScale > 1f)
                throw new ArgumentOutOfRangeException(nameof(verticalScale),
                    "LodVertical: the LOD vertical scale must be in (0, 1]; 1 measures LOD in the " +
                    "true world.");
            if (!(envelope >= 0.0) || double.IsInfinity(envelope))
                throw new ArgumentOutOfRangeException(nameof(envelope),
                    "LodVertical: the height envelope must be finite and not negative.");

            double anchor = surface.Type == SurfaceType.CubeSphere ? -envelope
                          : surface.Type == SurfaceType.Cylinder   ?  envelope
                          :                                           0.0;
            return new LodVertical { Flatten = 1.0 - verticalScale, Envelope = envelope, Anchor = anchor };
        }

        // A terrain height as the LOD sees it: A + s (h - A). Written h - Flatten (h - A) so the
        // identity returns h to the bit.
        public double MapHeight(double h) => h - Flatten * (h - Anchor);

        // A half-relief, or any height difference, as the LOD sees it: s times it, and the same
        // bits at the identity.
        public double MapRelief(double half) => half - Flatten * half;

        // An altitude above the base surface as the LOD sees it: MapHeight inside the envelope,
        // shifted and unscaled outside it.
        public double MapAltitude(double altitude)
            => altitude - Flatten * (math.clamp(altitude, -Envelope, Envelope) - Anchor);

        // A world point as the LOD sees it. For a vertex -- base point plus height along the
        // normal -- this is what the shader computes as positionWS - Flatten (elevation - Anchor)
        // topoUp; for the camera it is where TerrainRoot hands both the selector and the shader
        // to measure from.
        public double3 MapPoint(in SurfaceDef surface, double3 p)
        {
            if (Flatten == 0.0) return p;
            Altitude(surface, p, out double altitude, out double3 up);
            return p - up * (Flatten * (math.clamp(altitude, -Envelope, Envelope) - Anchor));
        }

        // Altitude above the base surface along the normal at the point's foot, and that normal:
        // the normals SurfaceMath.SurfaceFrame gives, which is what the shader's topoUp is. Radial
        // on a sphere; towards the axis inside a cylinder, whose terrain lines the inner wall;
        // +Y on a plane grid, whose base is y = 0.
        //
        // A cylinder's axis has no normal. The anchor puts it outside the envelope there, where
        // the map is the identity, so whatever direction this returns moves nothing.
        public static void Altitude(in SurfaceDef surface, double3 p, out double altitude, out double3 up)
        {
            switch (surface.Type)
            {
                case SurfaceType.CubeSphere:
                {
                    double r = math.length(p);
                    up       = r > 0.0 ? p / r : new double3(0.0, 1.0, 0.0);
                    altitude = r - surface.Radius;
                    return;
                }
                case SurfaceType.Cylinder:
                {
                    double rho = math.length(p.xy);
                    up       = rho > 0.0 ? new double3(-p.x / rho, -p.y / rho, 0.0) : new double3(1.0, 0.0, 0.0);
                    altitude = surface.Radius - rho;
                    return;
                }
                default:
                    up       = new double3(0.0, 1.0, 0.0);
                    altitude = p.y;
                    return;
            }
        }
    }
}
