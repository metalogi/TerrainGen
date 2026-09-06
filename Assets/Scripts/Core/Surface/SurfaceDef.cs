using Unity.Mathematics;

namespace Sonoma.Core.Surface
{
    public enum SurfaceType : byte { PlaneGrid, CubeSphere, Cylinder }

    // One root quad of the base mesh. Which fields are meaningful depends on SurfaceType.
    public struct RootQuad
    {
        public int     Face;            // CubeSphere: 0..5. Unused otherwise.
        public double3 Origin;          // PlaneGrid: tile min corner in world space.
        public double  Angle0, Angle1;  // Cylinder: angular range swept by u.
        public double  Z0, Z1;          // Cylinder: axial range swept by v.
    }

    // Where a root quad edge leads. Reversed means the along-edge parameter runs
    // opposite on the neighbour, which happens on 6 of the cube's 24 edge links.
    public readonly struct EdgeLink
    {
        public readonly int  Face;
        public readonly Edge Edge;
        public readonly bool Reversed;

        public EdgeLink(int face, Edge edge, bool reversed)
        {
            Face = face; Edge = edge; Reversed = reversed;
        }
    }

    // Immutable description of the world topology. Blittable and Burst-friendly:
    // no reference fields, so it can be copied into a job by value.
    public struct SurfaceDef
    {
        public SurfaceType Type;
        public double Radius;         // CubeSphere, Cylinder
        public double TileSize;       // PlaneGrid: edge length of one root tile
        public double Length;         // Cylinder: extent along the Z axis
        public int    Cols, Rows;     // PlaneGrid and Cylinder tiling
        public bool   TangentAdjust;  // CubeSphere spacing correction

        public int QuadCount => Type == SurfaceType.CubeSphere ? 6 : Cols * Rows;

        public static SurfaceDef CubeSphere(double radius, bool tangentAdjust = true)
            => new SurfaceDef
            {
                Type = SurfaceType.CubeSphere, Radius = radius,
                Cols = 1, Rows = 1, TangentAdjust = tangentAdjust
            };

        public static SurfaceDef PlaneGrid(double tileSize, int cols, int rows)
            => new SurfaceDef
            {
                Type = SurfaceType.PlaneGrid, TileSize = tileSize,
                Cols = cols, Rows = rows
            };

        // Rows defaults to whatever makes root quads closest to square.
        public static SurfaceDef Cylinder(double radius, double length, int cols, int rows = 0)
        {
            if (rows <= 0)
            {
                double quadWidth = 2.0 * math.PI * radius / cols;
                rows = (int)math.max(1.0, math.round(length / quadWidth));
            }
            return new SurfaceDef
            {
                Type = SurfaceType.Cylinder, Radius = radius, Length = length,
                Cols = cols, Rows = rows
            };
        }
    }
}
