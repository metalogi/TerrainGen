using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Sonoma.Core.Generation;
using Sonoma.Core.Surface;

namespace Sonoma.Tests
{
    // The geomorph invariant: a child driven to full morph *is* its parent.
    //
    // M2 wrote morph targets into TEXCOORD2/TEXCOORD3 on every vertex and had no way to
    // check them -- nothing read them until M3b. This is that check, and it is the reason
    // the `(i00, i11, i10), (i00, i01, i11)` triangulation is load-bearing and must not be
    // swapped for a flipping scheme: the correspondence below only works because every
    // child cell triangulates the same way round.
    //
    // Editor-only: it drives the real Burst jobs and a real Mesh.MeshData.
    public class GeomorphTests
    {
        // A plane tile rather than the Earth-radius sphere, so vertex magnitudes stay in the
        // hundreds and the tolerances below can be tight enough to mean something. The
        // correspondence itself is topology-independent -- it is arithmetic on vertex
        // indices, not geometry.
        const double TileSize   = 4096.0;
        const int    Resolution = 9;
        const int    Depth      = 2;

        // Positions are floats around two *different* double anchors, so they cannot be
        // bit-identical the way the (u, v) parameters in LodMathTests are. At this tile size
        // the float step is about 6e-5 m and each side contributes one, so 1e-3 m leaves
        // roughly four hundred times the expected error -- tight enough that landing on the
        // wrong vertex, which would be tens of metres out, cannot slip through.
        const double PositionTolerance = 1e-3;
        const double NormalTolerance   = 1e-3;

        SurfaceDef   _surface;
        RootQuad[]   _roots;
        HeightParams _params;

        [SetUp]
        public void SetUp()
        {
            _surface = SurfaceDef.PlaneGrid(TileSize, 1, 1);
            _roots   = SurfaceMath.BuildRoots(_surface);
            _params  = HeightParams.Create(_surface, Resolution, 0.0, 12, 200f, 0.5f, 2f, 42u);
        }

        [TearDown]
        public void TearDown() => ChunkMeshBuffers.DisposeCache();

        // The milestone's headline test.
        //
        // Two claims, and both matter. The first is that each child vertex's morph target is
        // the parent vertex it corresponds to -- position and normal, not just position, or
        // the shading pops at the swap even when the silhouette does not. The second is that
        // once every vertex has moved there, the child's triangles *are* the parent's: three
        // out of every four child cells collapse to zero area, and the fourth reproduces a
        // parent cell with the same winding.
        [Test]
        public void ChildAtFullMorphEqualsParent()
        {
            int R    = Resolution;
            int half = (R - 1) / 2;

            // Off-centre on purpose, so a bug that only works for quadrant 0 or for a node at
            // the origin has somewhere to show.
            var parentNode = new NodeId(0, Depth, 1, 2);
            var parent     = Build(parentNode);

            for (int q = 0; q < 4; q++)
            {
                var childNode = parentNode.Child(q);
                var child     = Build(childNode);
                int qu = q & 1, qv = (q >> 1) & 1;

                double worstPosition = 0.0, worstNormal = 0.0;

                // Claim 1: every child vertex's morph target is a real parent vertex.
                for (int j = 0; j < R; j++)
                for (int i = 0; i < R; i++)
                {
                    var cv = child.Verts[j * R + i];
                    var pv = parent.Verts[ParentIndex(qv, j, R) * R + ParentIndex(qu, i, R)];

                    // Compared in absolute world space: the two chunks store their vertices
                    // relative to different double anchors, so the local floats are not
                    // comparable directly.
                    double3 morphed = child.Anchor  + (double3)(float3)cv.MorphPosition.xyz;
                    double3 target  = parent.Anchor + (double3)pv.Position;

                    double dp = math.distance(morphed, target);
                    double dn = math.distance((double3)(float3)cv.MorphNormalElevation.xyz,
                                              (double3)pv.Normal);
                    worstPosition = math.max(worstPosition, dp);
                    worstNormal   = math.max(worstNormal,   dn);

                    Assert.Less(dp, PositionTolerance,
                        $"quadrant {q} vertex ({i},{j}): morph target is {dp:E3} m from the " +
                        "parent vertex it should coincide with");
                    Assert.Less(dn, NormalTolerance,
                        $"quadrant {q} vertex ({i},{j}): morph normal differs from the parent " +
                        $"normal by {dn:E3}");

                    // The depth packed into TEXCOORD2.w is what indexes the shader's range
                    // array; a wrong value there morphs the chunk against another depth's
                    // distances and cracks at the boundary.
                    Assert.AreEqual((float)childNode.Depth, cv.MorphPosition.w, 0f,
                        $"quadrant {q} vertex ({i},{j}): wrong depth packed into TEXCOORD2.w");

                    // And the elevation the shader blends its bands towards is the parent's
                    // own elevation -- bit-identical, not merely close, because both come
                    // from the same pure height function at the same band limit on a
                    // bit-identical surface position.
                    Assert.AreEqual(pv.TopoElevation.w, cv.MorphNormalElevation.w, 0f,
                        $"quadrant {q} vertex ({i},{j}): morph elevation is not the parent's");
                }

                // Claim 2: the fully morphed child triangulates exactly as the parent does.
                int surviving = 0;
                for (int cj = 0; cj < R - 1; cj++)
                for (int ci = 0; ci < R - 1; ci++)
                {
                    int a00 = ParentIndex(qv, cj,     R) * R + ParentIndex(qu, ci,     R);
                    int a10 = ParentIndex(qv, cj,     R) * R + ParentIndex(qu, ci + 1, R);
                    int a01 = ParentIndex(qv, cj + 1, R) * R + ParentIndex(qu, ci,     R);
                    int a11 = ParentIndex(qv, cj + 1, R) * R + ParentIndex(qu, ci + 1, R);

                    bool oddCell = (ci & 1) == 1 && (cj & 1) == 1;
                    if (!oddCell)
                    {
                        // Three cells in four collapse: at least two corners of each
                        // triangle land on the same parent vertex.
                        Assert.IsTrue(Degenerate(a00, a11, a10) && Degenerate(a00, a01, a11),
                            $"quadrant {q} cell ({ci},{cj}) should collapse at full morph but " +
                            "still spans distinct parent vertices");
                        continue;
                    }

                    // The fourth reproduces a parent cell -- the same three indices in the
                    // same order, so the winding survives and the mesh does not turn itself
                    // inside out halfway through the morph.
                    int pi = qu * half + ci / 2;
                    int pj = qv * half + cj / 2;
                    int p00 = pj * R + pi,       p10 = pj * R + pi + 1;
                    int p01 = (pj + 1) * R + pi, p11 = (pj + 1) * R + pi + 1;

                    Assert.IsFalse(Degenerate(a00, a11, a10) || Degenerate(a00, a01, a11),
                        $"quadrant {q} cell ({ci},{cj}) should survive at full morph but is degenerate");
                    Assert.AreEqual(new[] { p00, p11, p10 }, new[] { a00, a11, a10 },
                        $"quadrant {q} cell ({ci},{cj}): first triangle is not the parent's");
                    Assert.AreEqual(new[] { p00, p01, p11 }, new[] { a00, a01, a11 },
                        $"quadrant {q} cell ({ci},{cj}): second triangle is not the parent's");
                    surviving++;
                }

                Assert.AreEqual(half * half, surviving,
                    $"quadrant {q}: the surviving cells do not tile the parent quadrant");

                TestContext.WriteLine(
                    $"quadrant {q}: worst position error {worstPosition:E3} m, " +
                    $"worst normal error {worstNormal:E3}, {surviving} surviving cells");
            }
        }

        // Child vertex index -> parent vertex index along one axis, for the even sub-grid.
        // Odd child vertices share their even neighbour's target, which is what collapses
        // three cells in four.
        static int ParentIndex(int quadrant, int i, int R) => (quadrant * (R - 1) + (i & ~1)) / 2;

        static bool Degenerate(int a, int b, int c) => a == b || b == c || a == c;

        // The morph has to run in every pass. Morphing only ForwardLit leaves shadows and
        // the depth prepass on the unmorphed geometry, and the result -- shadow acne and
        // depth-test dropouts along LOD boundaries -- looks nothing like the cause, so it
        // is worth a cheap guard rather than an afternoon.
        [Test]
        public void ShaderMorphsInEveryPass()
        {
            string path = Path.Combine(Application.dataPath, "Shaders", "SonomaTerrainTriplanar.shader");
            Assert.IsTrue(File.Exists(path), $"shader not found at {path}");

            string source = File.ReadAllText(path);
            Assert.IsTrue(source.Contains("_SonomaMorphRanges"),
                "the shader declares no morph range array");

            // All four passes agreeing on k needs more than all four calling SonomaMorph:
            // they have to measure the distance from the same point. _WorldSpaceCameraPos is
            // not that point. URP restores it for the main-light shadow pass explicitly
            // (MainLightShadowCasterPass calls ShadowUtils.SetCameraPosition, commented "not
            // set for passes executed before normal rendering") and AdditionalLightsShadow-
            // CasterPass does not, so a scene with a shadow-casting point or spot light and no
            // shadowed directional light renders the ShadowCaster pass against whatever was
            // last bound -- a stale frame, or another camera -- and casts shadows from
            // geometry at a morph state ForwardLit never drew. Same shadow-acne-along-LOD-
            // boundaries failure this test exists to prevent, reached from the other side.
            //
            // Scoped to the function body, because the comment above it names the builtin it
            // is deliberately not using.
            int fnStart = source.IndexOf("float SonomaMorphFactor(", System.StringComparison.Ordinal);
            Assert.Greater(fnStart, 0, "SonomaMorphFactor is missing from the shader");
            int fnEnd = source.IndexOf("\n        }", fnStart, System.StringComparison.Ordinal);
            Assert.Greater(fnEnd, fnStart, "SonomaMorphFactor has no body");

            string factor = source.Substring(fnStart, fnEnd - fnStart);
            Assert.IsTrue(factor.Contains("_SonomaViewPosition"),
                "the morph distance is not measured from the view position TerrainRoot pushes");
            Assert.IsFalse(factor.Contains("_WorldSpaceCameraPos"),
                "the morph distance is measured from _WorldSpaceCameraPos, which URP does not " +
                "set for the additional-lights shadow pass; see TerrainRoot.PushShaderGlobals");

            foreach (string pass in new[] { "ForwardLit", "ShadowCaster", "DepthOnly", "DepthNormals" })
            {
                int start = source.IndexOf("Name \"" + pass + "\"", System.StringComparison.Ordinal);
                Assert.Greater(start, 0, $"pass {pass} is missing from the shader");

                int end = source.IndexOf("ENDHLSL", start, System.StringComparison.Ordinal);
                Assert.Greater(end, start, $"pass {pass} has no HLSL block");

                string body = source.Substring(start, end - start);
                Assert.IsTrue(body.Contains("SonomaMorph"),
                    $"pass {pass} does not apply the geomorph");
                Assert.IsTrue(body.Contains("TEXCOORD2"),
                    $"pass {pass} does not read the morph attribute");
            }
        }

        // -- Building a chunk through the real job path ------------------------

        struct Built
        {
            public double3         Anchor;
            public TerrainVertex[] Verts;
        }

        Built Build(NodeId node)
        {
            var root    = _roots[node.Quad];
            int R       = _params.Resolution;
            int samples = ChunkMeshLayout.SampleCount(R);
            int coarse  = ChunkMeshLayout.CoarseCount(R);

            var basePoints    = new NativeArray<double3>(samples, Allocator.TempJob);
            var baseNormals   = new NativeArray<float3>(samples,  Allocator.TempJob);
            var heights       = new NativeArray<float>(samples,   Allocator.TempJob);
            var coarsePoints  = new NativeArray<double3>(coarse,  Allocator.TempJob);
            var coarseNormals = new NativeArray<float3>(coarse,   Allocator.TempJob);
            var coarseHeights = new NativeArray<float>(coarse,    Allocator.TempJob);
            var heightBounds  = new NativeArray<float>(2,         Allocator.TempJob);

            var meshData = Mesh.AllocateWritableMeshData(1);
            var data     = meshData[0];
            var attrs    = new NativeArray<VertexAttributeDescriptor>(
                               ChunkMeshBuffers.VertexAttributes(), Allocator.Temp);
            data.SetVertexBufferParams(ChunkMeshLayout.VertexCount(R), attrs);
            attrs.Dispose();
            data.SetIndexBufferParams(ChunkMeshLayout.IndexCount(R), IndexFormat.UInt16);
            data.subMeshCount = 1;

            double3 anchor = SurfaceMath.SurfacePoint(_surface, root,
                                 0.5 * (node.UMin + node.UMax), 0.5 * (node.VMin + node.VMax));

            new HeightSampleJob
            {
                Surface = _surface, Root = root, Node = node, Params = _params,
                MaxOctave = _params.MaxOctave(node.Depth), Resolution = R,
                BasePoints = basePoints, BaseNormals = baseNormals, Heights = heights,
            }.Schedule(samples, 64).Complete();

            new CoarseHeightSampleJob
            {
                Surface = _surface, Root = root, Node = node, Params = _params,
                MaxOctave = _params.MaxOctave(node.Depth - 1), Resolution = R,
                BasePoints = coarsePoints, BaseNormals = coarseNormals, Heights = coarseHeights,
            }.Schedule(coarse, 64).Complete();

            new ChunkMeshJob
            {
                Resolution = R, Anchor = anchor, SkirtDepth = 0f, Depth = node.Depth,
                BasePoints = basePoints, BaseNormals = baseNormals, Heights = heights,
                CoarseBasePoints = coarsePoints, CoarseBaseNormals = coarseNormals,
                CoarseHeights = coarseHeights,
                Mesh = data, HeightBounds = heightBounds,
            }.Schedule().Complete();

            var verts = data.GetVertexData<TerrainVertex>().ToArray();

            meshData.Dispose();
            basePoints.Dispose(); baseNormals.Dispose(); heights.Dispose();
            coarsePoints.Dispose(); coarseNormals.Dispose(); coarseHeights.Dispose();
            heightBounds.Dispose();

            return new Built { Anchor = anchor, Verts = verts };
        }
    }
}
