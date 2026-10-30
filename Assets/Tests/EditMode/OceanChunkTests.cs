using System;
using NUnit.Framework;
using Unity.Mathematics;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The ocean through the real mesh job (Docs/Ocean_A_Plan.md, Step 4): the kind it decides, the
    // water it writes, the water's skirt, and the mesh it writes with no ocean.
    //
    // Editor-only: it drives the real Burst jobs and a real Mesh.MeshData, through ChunkJobRunner.
    public class OceanChunkTests
    {
        [TearDown]
        public void TearDown() => ChunkMeshBuffers.DisposeCache();

        // An overlap and waves far past any relief here, so every chunk is Coast and draws both grids;
        // a sea far above it, with no overlap, so every chunk is Ocean.
        static OceanParams Coast(float sea) => new OceanParams
            { Enabled = true, SeaLevel = sea, SeaLevelFloat = sea, TerrainOverlap = 1e6f, WaveAmplitude = 1e6f };
        static OceanParams Ocean(float sea) => new OceanParams
            { Enabled = true, SeaLevel = sea, SeaLevelFloat = sea };

        // A1, on EarthStress's real terrain: its eroded maps from the Burst build, its height
        // parameters and its sea, over a sweep of depth-7 nodes on every face (80 km chunks). For each,
        // the test takes the drawn range from the sample jobs' own heights -- the fine grid and the
        // morph targets -- classifies it, and holds the mesh job to that: the range it reports, the
        // kind, the vertex and index counts, the submeshes, and which grid is where. The sweep must
        // find all three kinds, so it finds its nodes from the terrain as built rather than from
        // hand-picked addresses a retune of the continents could move.
        [Test]
        public void JobDecidesTheKindFromWhatItDraws()
        {
            var m     = MacroTests.For("EarthStress");
            var hp    = MacroTests.SceneParams(m, MacroTests.RoughParams(m.Scene));
            var c     = m.ErodedConfig;
            var p     = MacroParams.Create(m.Surface, hp, c);
            var roots = SurfaceMath.BuildRoots(m.Surface);
            using var maps = MacroMaps.Build(m.Surface, roots, p, ContinentParams.Create(m.Surface, p, c),
                                             hp, ErosionParams.Create(m.Surface, hp, p, c));
            float sea   = m.Erosion.Value.BaseLevel;
            var   ocean = OceanParams.Create(true, sea, 0f, hp.Resolution, maps.MaxAbsHeight(hp), true, sea);

            int R = hp.Resolution, n = ChunkMeshLayout.VertexCount(R), I = ChunkMeshLayout.IndexCount(R);
            const int Depth = 7, Across = 6;
            var counts = new int[3];
            for (int q = 0; q < roots.Length; q++)
            for (int y = 0; y < Across; y++)
            for (int x = 0; x < Across; x++)
            {
                int span = 1 << Depth;
                var node = new NodeId(q, Depth, (2 * x + 1) * span / (2 * Across), (2 * y + 1) * span / (2 * Across));
                var b    = ChunkJobRunner.Build(m.Surface, roots, hp, node, maps, ocean: ocean);
                string at = $"node q{node.Quad} d{node.Depth} ({node.X},{node.Y})";

                float lo = float.MaxValue, hi = float.MinValue;
                for (int j = 0; j < R; j++)
                for (int i = 0; i < R; i++)
                {
                    float h = b.Heights[ChunkJobRunner.Sample(i, j, R)], hc = b.CoarseHeights[ChunkJobRunner.CoarseSample(i, j, R)];
                    lo = math.min(lo, math.min(h, hc));
                    hi = math.max(hi, math.max(h, hc));
                }
                var kind = ChunkKinds.Classify(lo, hi, ocean.SeaLevelFloat, ocean.TerrainOverlap, ocean.WaveAmplitude);
                Assert.AreEqual(lo, b.HeightBounds[3], $"{at}: the drawn range's low end");
                Assert.AreEqual(hi, b.HeightBounds[4], $"{at}: the drawn range's high end");
                Assert.AreEqual(kind, b.Kind, $"{at}: drawn range {lo:F0} .. {hi:F0} m against a sea at {sea} m");
                counts[(int)kind]++;

                Assert.AreEqual(ChunkMeshLayout.VertexCount(R, kind), b.Verts.Length,  $"{at}: {kind} vertices");
                Assert.AreEqual(ChunkMeshLayout.IndexCount(R, kind),  b.Indices.Length, $"{at}: {kind} indices");
                Assert.AreEqual(kind == ChunkKind.Coast ? 2 : 1, b.SubMeshes.Length,     $"{at}: {kind} submeshes");
                for (int s = 0; s < b.SubMeshes.Length; s++)
                {
                    Assert.AreEqual(s * I, b.SubMeshes[s].indexStart, $"{at}: submesh {s} start");
                    Assert.AreEqual(I,     b.SubMeshes[s].indexCount, $"{at}: submesh {s} count");
                    Assert.AreEqual(0,     b.SubMeshes[s].baseVertex, $"{at}: submesh {s} base vertex");
                }

                // Which grid is where: the terrain's elevation is the height, the water's the sea.
                int k0 = ChunkJobRunner.Sample(R / 2, R / 2, R), v0 = (R / 2) * R + R / 2;
                if (kind != ChunkKind.Ocean)
                    Assert.AreEqual(b.Heights[k0], b.Verts[v0].TopoElevation.w, $"{at}: terrain at [0, n)");
                if (kind != ChunkKind.Land)
                    Assert.AreEqual(ocean.SeaLevelFloat,
                        b.Verts[ChunkMeshLayout.WaterBase(R, kind) + v0].TopoElevation.w, $"{at}: water at its base");
                if (kind == ChunkKind.Coast)
                    Assert.AreEqual(n, ChunkMeshLayout.WaterBase(R, kind));
            }
            TestContext.WriteLine($"Earth, {6 * Across * Across} depth-{Depth} nodes: " +
                                  $"{counts[0]} Land, {counts[1]} Coast, {counts[2]} Ocean");
            Assert.Greater(counts[(int)ChunkKind.Land],  0, "the sweep found no Land chunk");
            Assert.Greater(counts[(int)ChunkKind.Coast], 0, "the sweep found no Coast chunk");
            Assert.Greater(counts[(int)ChunkKind.Ocean], 0, "the sweep found no Ocean chunk");
        }

        // C1 and A8: every water vertex is the base surface point offset along its normal by the sea
        // level, SurfaceMath's analytic surface and not an interpolation, to within the float
        // rounding of an anchor-relative position. On all three topologies, at a Coast chunk's water
        // base and an Ocean chunk's. Inside a cylinder the normal points at the axis, so the sea there
        // lies at the radius less the sea level.
        [Test]
        public void SeaSurfaceIsTheBaseSurfaceOffset()
        {
            var surfaces = new[]
            {
                (name: "CubeSphere", s: SurfaceDef.CubeSphere(1000.0)),
                (name: "PlaneGrid",  s: SurfaceDef.PlaneGrid(4096.0, 1, 1)),
                (name: "Cylinder",   s: SurfaceDef.Cylinder(1000.0, 4000.0, 8)),
            };
            using var none = MacroMaps.Disabled();
            foreach (var (name, s) in surfaces)
            {
                var roots = SurfaceMath.BuildRoots(s);
                var hp    = HeightParams.Create(s, 9, 0.0, 12, 20f, 0.5f, 2f, 42u);
                var node  = new NodeId(0, 3, 2, 5);
                int R     = hp.Resolution;

                foreach (var (ocean, kind) in new[] { (Coast(15f), ChunkKind.Coast), (Ocean(500f), ChunkKind.Ocean) })
                {
                    var b = ChunkJobRunner.Build(s, roots, hp, node, none, ocean: ocean);
                    Assert.AreEqual(kind, b.Kind, $"{name}: the sea at {ocean.SeaLevel} m");
                    int wb = ChunkMeshLayout.WaterBase(R, kind);
                    double worst = 0.0;
                    for (int j = 0; j < R; j++)
                    for (int i = 0; i < R; i++)
                    {
                        ChunkGrid.VertexUV(node, i, j, R, out double u, out double v);
                        SurfaceMath.SurfaceFrame(s, roots[0], u, v, out double3 point, out float3 normal);
                        double3 sea   = point + (double3)normal * ocean.SeaLevel;
                        var     vert  = b.Verts[wb + j * R + i];
                        double3 drawn = b.Anchor + (double3)vert.Position;
                        double  tol   = 4e-7 * (math.length((double3)vert.Position) + 1.0);
                        double  d     = math.distance(drawn, sea);
                        worst = math.max(worst, d / tol);
                        Assert.Less(d, tol, $"{name} {kind}, water vertex ({i},{j}) is {d:E3} m off the sea surface");
                        // Burst's trigonometry is not managed code's to the last bit (CLAUDE.md), so
                        // the base normal is compared to a float's rounding, not to the bit.
                        Assert.Less(math.distance(normal, vert.Normal), 1e-6f,
                            $"{name} {kind}, water vertex ({i},{j}): the normal is not the base surface's");
                    }
                    TestContext.WriteLine($"{name} {kind}: worst {worst:F3} of the tolerance");
                }
            }
        }

        // The water's skirt, as JobHangsTheSkirtItReports checks the terrain's: every water skirt
        // bottom, and its morph target, hangs ChunkMeshLayout.WaterSkirtDrop below its top along the
        // top's up, the terrain's skirts in the same chunk keep the terrain's drop, and the reported
        // bounds are the box round every vertex of both grids and their morph targets.
        [Test]
        public void WaterSkirtHangsItsDrop()
        {
            var s     = SurfaceDef.CubeSphere(1000.0);
            var roots = SurfaceMath.BuildRoots(s);
            var hp    = HeightParams.Create(s, 9, 0.0, 12, 20f, 0.5f, 2f, 42u);
            using var none = MacroMaps.Disabled();
            const float SkirtDepth = 1.5f, Fraction = 0.8f, Sag = 7.25f;
            float waterDrop = ChunkMeshLayout.WaterSkirtDrop(SkirtDepth, Fraction, Sag);
            Assert.AreEqual(SkirtDepth + Sag, waterDrop, 1e-5f, "the water's drop has no relief term");

            var b = ChunkJobRunner.Build(s, roots, hp, new NodeId(1, 3, 2, 5), none, SkirtDepth, Fraction, Sag,
                                         Coast(10f), waterDrop);
            Assert.AreEqual(ChunkKind.Coast, b.Kind);
            int R = hp.Resolution, n = ChunkMeshLayout.VertexCount(R);
            float drop = b.HeightBounds[2];
            Assert.AreEqual(ChunkMeshLayout.SkirtDrop(SkirtDepth, Fraction, b.HeightBounds[1] - b.HeightBounds[0], Sag),
                            drop, drop * 1e-6, "the terrain's drop");

            foreach (var (wb, d, what) in new[] { (0, drop, "terrain"), (n, waterDrop, "water") })
            for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < R; k++)
            {
                var top    = b.Verts[wb + ChunkMeshLayout.SkirtTopIndex(R, edge, k)];
                var bottom = b.Verts[wb + ChunkMeshLayout.SkirtBottomIndex(R, edge, k)];
                float3 up  = top.TopoElevation.xyz;
                Assert.Less(math.length(top.Position - up * d - bottom.Position), 1e-4f,
                    $"{what} edge {edge}, post {k}: the skirt bottom is not {d} m below its top");
                Assert.Less(math.length(top.MorphPosition.xyz - up * d - bottom.MorphPosition.xyz), 1e-4f,
                    $"{what} edge {edge}, post {k}: the skirt bottom's morph target is not {d} m below its top's");
            }

            float3 lo = float.MaxValue, hi = float.MinValue;
            foreach (var v in b.Verts)
            {
                lo = math.min(lo, math.min(v.Position, v.MorphPosition.xyz));
                hi = math.max(hi, math.max(v.Position, v.MorphPosition.xyz));
            }
            Assert.AreEqual(lo, b.VertexBounds[0], "the reported bounds' minimum is not the vertices'");
            Assert.AreEqual(hi, b.VertexBounds[1], "the reported bounds' maximum is not the vertices'");
        }

        // Ground rule 1: with no ocean the job writes the mesh it always did. Held to the terrain half
        // of a Coast chunk of the same node, which is today's code path writing today's values, byte
        // for byte: vertices, indices, the one submesh, the bounds the selector reads. Over a real
        // macro layer would add nothing the comparison needs; on a cube sphere, whose winding flips.
        [Test]
        public void OceanOffBuildsTodaysMesh()
        {
            var s     = SurfaceDef.CubeSphere(1000.0);
            var roots = SurfaceMath.BuildRoots(s);
            var hp    = HeightParams.Create(s, 9, 0.0, 12, 20f, 0.5f, 2f, 42u);
            using var none = MacroMaps.Disabled();
            var node  = new NodeId(4, 3, 6, 1);
            int R = hp.Resolution, n = ChunkMeshLayout.VertexCount(R), I = ChunkMeshLayout.IndexCount(R);

            var off   = ChunkJobRunner.Build(s, roots, hp, node, none, 1f, 0.5f, 3f);
            var coast = ChunkJobRunner.Build(s, roots, hp, node, none, 1f, 0.5f, 3f, Coast(5f), 4f);

            Assert.AreEqual(ChunkKind.Land, off.Kind);
            Assert.AreEqual(n, off.Verts.Length, "vertices");
            Assert.AreEqual(I, off.Indices.Length, "indices");
            Assert.AreEqual(1, off.SubMeshes.Length, "submeshes");
            Assert.AreEqual(0, off.SubMeshes[0].indexStart);
            Assert.AreEqual(I, off.SubMeshes[0].indexCount);
            CollectionAssert.AreEqual(ChunkMeshBuffers.SharedIndices(R, SurfaceMath.UvFrameIsRightHanded(s)).ToArray(),
                                      off.Indices, "the single shared buffer");

            for (int k = 0; k < n; k++)
                Assert.IsTrue(SameBits(off.Verts[k], coast.Verts[k]),
                    $"vertex {k} differs between no ocean and a Coast chunk's terrain");
            for (int k = 0; k < I; k++)
                Assert.AreEqual(off.Indices[k], coast.Indices[k], $"index {k}");
            for (int k = 0; k < 3; k++)
                Assert.AreEqual(BitConverter.SingleToInt32Bits(off.HeightBounds[k]),
                                BitConverter.SingleToInt32Bits(coast.HeightBounds[k]), $"HeightBounds[{k}]");
        }

        static bool SameBits(TerrainVertex a, TerrainVertex b) =>
            math.all(math.asuint(a.Position) == math.asuint(b.Position)) &&
            math.all(math.asuint(a.Normal)   == math.asuint(b.Normal)) &&
            a.Splat == b.Splat && a.MorphSplat == b.MorphSplat &&
            math.all(math.asuint(a.TopoElevation)        == math.asuint(b.TopoElevation)) &&
            math.all(math.asuint(a.MorphPosition)        == math.asuint(b.MorphPosition)) &&
            math.all(math.asuint(a.MorphNormalElevation) == math.asuint(b.MorphNormalElevation));
    }
}
