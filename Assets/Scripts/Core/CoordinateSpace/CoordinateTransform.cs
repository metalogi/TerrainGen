using System;
using Unity.Mathematics;

namespace Sonoma.Core.CoordinateSpace
{
    public static class CoordinateTransform
    {
        public static double3 ToWorldPosition(double u, double v, double height, BaseMeshQuad quad)
        {
            switch (quad.Type)
            {
                case SurfaceType.Sphere:
                {
                    double  lat  = math.lerp(quad.Lat0, quad.Lat1, v);
                    double  lon  = math.lerp(quad.Lon0, quad.Lon1, u);
                    double3 surf = SpherePoint(quad.SphereRadius, lat, lon);
                    float3  n    = math.normalize((float3)(surf / quad.SphereRadius));
                    return surf + (double3)(n * (float)height);
                }

                case SurfaceType.Cylinder:
                {
                    double  angle  = math.lerp(quad.Angle0, quad.Angle1, u);
                    double  z      = math.lerp(quad.CylZ0,  quad.CylZ1,  v);
                    double3 radial = new double3(Math.Cos(angle), Math.Sin(angle), 0.0);
                    float3  n      = -math.normalize((float3)radial); // inward
                    return quad.CylRadius * radial + new double3(0.0, 0.0, z) + (double3)(n * (float)height);
                }

                default: // Plane — bilinear interpolation is exact for flat surfaces
                {
                    double oneMinusU = 1.0 - u;
                    double oneMinusV = 1.0 - v;

                    double3 pos = quad.P00 * (oneMinusU * oneMinusV)
                                + quad.P10 * (u         * oneMinusV)
                                + quad.P01 * (oneMinusU * v)
                                + quad.P11 * (u         * v);

                    float3 n = quad.N00 * (float)(oneMinusU * oneMinusV)
                             + quad.N10 * (float)(u         * oneMinusV)
                             + quad.N01 * (float)(oneMinusU * v)
                             + quad.N11 * (float)(u         * v);

                    return pos + (double3)(math.normalize(n) * (float)height);
                }
            }
        }

        static double3 SpherePoint(double radius, double lat, double lon)
            => new double3(radius * Math.Cos(lat) * Math.Cos(lon),
                           radius * Math.Sin(lat),
                           radius * Math.Cos(lat) * Math.Sin(lon));
    }
}
