using Unity.Mathematics;

namespace Sonoma.Core.CoordinateSpace
{
    public enum SurfaceType { Plane, Sphere, Cylinder }

    public struct BaseMeshQuad
    {
        public SurfaceType Type;

        // Plane: corner positions and normals (bilinear interpolation is exact for flat surfaces)
        public double3 P00, P10, P01, P11;
        public float3  N00, N10, N01, N11;

        // Sphere: parametric range — CoordinateTransform interpolates lat/lon and computes analytically
        public double SphereRadius;
        public double Lat0, Lat1;    // latitude  at v=0 and v=1
        public double Lon0, Lon1;   // longitude at u=0 and u=1

        // Cylinder: parametric range — axis along Z, inward normals
        public double CylRadius;
        public double Angle0, Angle1;  // angle at u=0 and u=1
        public double CylZ0,  CylZ1;  // z     at v=0 and v=1

        public override string ToString() => $"BaseMeshQuad(Type={Type})";
    }
}
