using System;
using Unity.Mathematics;
using Sonoma.Core.Surface;

namespace Sonoma.Core.Generation
{
    // Where every texel of the macro maps lives, which pyramid level a depth reads, and which
    // texels are copies of another quad's.
    //
    // Each root quad has a pyramid of LevelCount levels. Level L has n_L = Cells0 x 2^L texel
    // cells across the quad, Cells0 being a chunk's R - 1, so level L's texel spacing is exactly
    // depth L's nominal vertex spacing: at every depth up to the top level the chunk's vertices
    // land on texels, and the read there is the stored value with no interpolation at all
    // (MacroSampler). Deeper chunks interpolate the top level. A single map per quad would have
    // the spacing of one depth only, and every coarser chunk would point-sample content it
    // cannot represent -- aliasing, at exactly the depths seen from orbit, where continents are
    // the point. Docs/M5_MacroLayer_Plan.md, C1.
    //
    // A level stores texels -Border .. n_L + Border on each axis, row-major (j, then i). The
    // border is three texels because of the furthest read, the coarse job at depth 0. Its grid
    // runs two depth-0 vertices past the quad edge, and it reads level 0, whose texels are
    // depth-0 vertices, so it lands two texels out. Every other read reaches at most one texel
    // out, because MacroLevel(d) <= d. MacroSampler puts a read on a texel in a cell whose
    // stencil reaches one texel further, so a read two texels out needs texel 3. With a border of
    // two that read extrapolated with weights (2, -3, 3, -1). It is not unused: roots morph
    // towards their coarse targets from far out (LodMath.MorphRange(0)), and the extrapolation
    // tilted the coarse normals along every root-quad edge seen from orbit.
    //
    // Levels are packed one after another within a quad, quads one after another, so the whole
    // surface is a single flat array.
    //
    // UnityEngine-free and blittable, so the jobs, the main thread and the out-of-Editor harness
    // all address the maps through the same arithmetic.
    public readonly struct MacroLayout
    {
        public const int Border = 3;

        public readonly int Cells0;       // texel cells across a quad at level 0: R - 1
        public readonly int LevelCount;
        public readonly int QuadCount;
        public readonly int QuadStride;   // texels per quad, every level

        public MacroLayout(int cells0, int levelCount, int quadCount)
        {
            if (cells0 < 2)
                throw new ArgumentOutOfRangeException(nameof(cells0),
                    "MacroLayout: a level-0 map needs at least two texel cells across.");
            if (levelCount < 1)
                throw new ArgumentOutOfRangeException(nameof(levelCount),
                    "MacroLayout: the pyramid needs at least one level.");
            if (quadCount < 1)
                throw new ArgumentOutOfRangeException(nameof(quadCount),
                    "MacroLayout: the surface has no root quads.");

            // Summed in long and refused rather than wrapped: an int overflow here would size
            // the array small and every read past the wrap would land in another quad's texels.
            long stride = 0;
            for (int l = 0; l < levelCount; l++)
            {
                long w = ((long)cells0 << l) + 1 + 2 * Border;
                stride += w * w;
                if (l > 24 || stride * quadCount > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(levelCount),
                        "MacroLayout: the pyramid has more texels than one array can address; " +
                        "lower LevelCount.");
            }

            Cells0     = cells0;
            LevelCount = levelCount;
            QuadCount  = quadCount;
            QuadStride = (int)stride;
        }

        public int TexelCount => QuadCount * QuadStride;

        // Texel cells across a quad at this level, n_L.
        public int Cells(int level) => Cells0 << level;

        // Stored texels per row at this level, border included.
        public int Width(int level) => (Cells0 << level) + 1 + 2 * Border;

        public int LevelOffset(int level)
        {
            int offset = 0;
            for (int l = 0; l < level; l++)
            {
                int w = Width(l);
                offset += w * w;
            }
            return offset;
        }

        // i and j run from -Border to Cells(level) + Border.
        public int Index(int quad, int level, int i, int j)
            => quad * QuadStride + LevelOffset(level) + (j + Border) * Width(level) + (i + Border);

        public void Decode(int index, out int quad, out int level, out int i, out int j)
        {
            quad = index / QuadStride;
            int r = index - quad * QuadStride;

            level = 0;
            int w = Width(0);
            while (r >= w * w)
            {
                r -= w * w;
                level++;
                w = Width(level);
            }

            int row = r / w;
            j = row - Border;
            i = r - row * w - Border;
        }

        // The pyramid level a chunk at this depth reads.
        //
        // A function of depth alone, never of measured node size, for the reason
        // HeightParams.MaxOctave is: two same-depth neighbours must read the same level, or they
        // evaluate different functions along the edge they share. The coarse sample job passes
        // depth - 1, so the morph target is the parent's macro as well as its octaves; at depth
        // 0 that clamps to level 0. Roots morph towards it from far out, so it is drawn.
        public int MacroLevel(int depth) => math.clamp(depth, 0, LevelCount - 1);

        // ── Shared edges ─────────────────────────────────────────────────────
        //
        // A bicubic read near a root-quad edge uses that quad's texels only, so unless both
        // quads sharing an edge store identical edge texels the macro differs across the edge.
        // That would be a crack in the BASE height, kilometres of it. With a pure generator the
        // two sides would differ only by what the parameterisations' 1.3 nm disagreement moves a
        // smooth field, but the maps exist for what a pure generator cannot do -- erosion will
        // not keep two faces' edge rows equal by accident -- so the agreement is structural:
        //
        //   * Every texel on an edge row, including the row's one-texel extension past each end
        //     (the along-edge stencil near a corner reads it), is written by the edge's owner, the
        //     quad with the lower index, and copied into the other through the link, reversal
        //     included. A cylinder of one column links its East edge to its own West; there the
        //     lower edge number owns it.
        //   * A corner texel is shared by three quads on a cube sphere and up to four on a plane
        //     grid or cylinder, and comes from the lowest of them.
        //   * An extension texel of one edge row is a border texel of the other edge at that
        //     corner. The copy wins: reads exactly on that other edge give it zero weight.
        //
        // Every source is a texel nothing copies into, so the copies can be applied in any
        // order, once, after generation. MacroSampler's read is then exact under reversal, and a
        // sample at a shared edge point is bit-identical from both quads at every level.

        // The texel this one is a copy of: false, with the source set to (quad, i, j) itself,
        // for a texel its own quad owns.
        public bool EdgeSource(in SurfaceDef s, int quad, int level, int i, int j,
                               out int srcQuad, out int srcI, out int srcJ)
        {
            srcQuad = quad; srcI = i; srcJ = j;
            int n = Cells(level);

            bool onI = i == 0 || i == n;     // on the West or East edge row
            bool onJ = j == 0 || j == n;     // on the South or North edge row

            if (onI && onJ)
            {
                CornerSource(s, n, quad, i, j, out srcQuad, out srcI, out srcJ);
                return srcQuad != quad || srcI != i || srcJ != j;
            }

            Edge edge;
            int  along;
            if (onJ && i >= -1 && i <= n + 1)      { edge = j == 0 ? Edge.South : Edge.North; along = i; }
            else if (onI && j >= -1 && j <= n + 1) { edge = i == 0 ? Edge.West  : Edge.East;  along = j; }
            else return false;

            if (!SurfaceMath.QuadEdgeLink(s, quad, edge, out EdgeLink link)) return false;

            bool neighbourOwns = link.Face < quad || (link.Face == quad && link.Edge < edge);
            if (!neighbourOwns) return false;

            srcQuad = link.Face;
            OnEdge(link.Edge, link.Reversed ? n - along : along, n, out srcI, out srcJ);
            return true;
        }

        // Walks round the corner in both directions from this quad, crossing alternately the
        // edge row each quad arrived by and the other one, and keeps the lowest (quad, i, j)
        // it meets. On a closed corner the first walk comes back round; on an open one (a
        // cylinder's end, a plane grid's rim) each walk stops at an edge with no link and the
        // two between them visit everything.
        static void CornerSource(in SurfaceDef s, int n, int quad, int ci, int cj,
                                 out int srcQuad, out int srcI, out int srcJ)
        {
            srcQuad = quad; srcI = ci; srcJ = cj;

            for (int dir = 0; dir < 2; dir++)
            {
                int  q = quad, i = ci, j = cj;
                bool crossSouthNorth = dir == 0;

                for (int step = 0; step < 8; step++)
                {
                    Edge e = crossSouthNorth ? (j == 0 ? Edge.South : Edge.North)
                                             : (i == 0 ? Edge.West  : Edge.East);
                    if (!SurfaceMath.QuadEdgeLink(s, q, e, out EdgeLink link)) break;

                    int along = crossSouthNorth ? i : j;
                    q = link.Face;
                    OnEdge(link.Edge, link.Reversed ? n - along : along, n, out i, out j);
                    if (q == quad && i == ci && j == cj) break;

                    if (q < srcQuad || (q == srcQuad && (i < srcI || (i == srcI && j < srcJ))))
                    {
                        srcQuad = q; srcI = i; srcJ = j;
                    }

                    // Arrived on a West or East edge: the other edge at this corner is South or
                    // North, and the other way round.
                    crossSouthNorth = link.Edge == Edge.West || link.Edge == Edge.East;
                }
            }
        }

        // Texel coordinates of a point `along` an edge row, the edge's own parameter direction.
        static void OnEdge(Edge e, int along, int n, out int i, out int j)
        {
            switch (e)
            {
                case Edge.South: i = along; j = 0;     return;
                case Edge.North: i = along; j = n;     return;
                case Edge.West:  i = 0;     j = along; return;
                default:         i = n;     j = along; return;   // East
            }
        }

        // Every (destination, source) pair of flat indices the edge rule copies, over every
        // quad and level. Applied once after generation, in any order (see above); MacroMaps and
        // the tests' managed builder apply the same list.
        public int2[] EdgeCopies(in SurfaceDef s)
        {
            var copies = new System.Collections.Generic.List<int2>();
            for (int q = 0; q < QuadCount; q++)
            for (int l = 0; l < LevelCount; l++)
            {
                int n = Cells(l);
                // South and North rows in full, extensions and corners included; West and East
                // rows without the corners, which the first two already visited.
                for (int a = -1; a <= n + 1; a++)
                {
                    AddCopy(s, copies, q, l, a, 0);
                    AddCopy(s, copies, q, l, a, n);
                    if (a == 0 || a == n) continue;
                    AddCopy(s, copies, q, l, 0, a);
                    AddCopy(s, copies, q, l, n, a);
                }
            }
            return copies.ToArray();
        }

        // The same pairs for one level only, in the order MacroPyramid applies them: a derived
        // level gets its copies after its interior and its halo are written.
        public int2[] EdgeCopies(in SurfaceDef s, int level)
        {
            var copies = new System.Collections.Generic.List<int2>();
            int n = Cells(level);
            for (int q = 0; q < QuadCount; q++)
            for (int a = -1; a <= n + 1; a++)
            {
                AddCopy(s, copies, q, level, a, 0);
                AddCopy(s, copies, q, level, a, n);
                if (a == 0 || a == n) continue;
                AddCopy(s, copies, q, level, 0, a);
                AddCopy(s, copies, q, level, n, a);
            }
            return copies.ToArray();
        }

        void AddCopy(in SurfaceDef s, System.Collections.Generic.List<int2> copies,
                     int q, int l, int i, int j)
        {
            if (EdgeSource(s, q, l, i, j, out int sq, out int si, out int sj))
                copies.Add(new int2(Index(q, l, i, j), Index(sq, l, si, sj)));
        }

        // ── Halo ─────────────────────────────────────────────────────────────
        //
        // A level the generator fills computes its border texels itself, continuing this quad's
        // own parameterisation past the edge. A level MacroPyramid derives from the one above has
        // no function to continue: its border must come from the neighbouring quad, the texels the
        // border would overlap if the two quads were laid side by side
        // (Docs/Continents_Plan.md, K5).
        //
        //   * A side texel, `depth` texels past one edge and `along` it, takes the neighbour's texel
        //     at `along` on the linked edge (reversed if the link is) and `depth` texels in from it.
        //   * A corner block, past two edges at once, has no neighbour on a cube, where three faces
        //     meet; it takes what the side texel beside it, on its South or North side, takes.
        //   * Past an edge with no link (a cylinder's rim, a plane grid's outer edge) there is
        //     nothing, and the border repeats this quad's own edge texel.
        //
        // The source is then resolved through EdgeSource to the texel its owner stores, so every
        // source lies in [0, n] of some quad and is a texel neither the halo nor the edge copies
        // write. The fill is one gather, in any order, before or after the copies. Exactness does
        // not matter here the way it does on the edge line: C2 makes reads exact on the edge,
        // where shared vertices lie, and a border texel only shapes reads just inside one quad.

        // The texel this border texel is filled from: false, with the source set to (quad, i, j),
        // for a texel inside [0, n] on both axes, which is not a border texel.
        public bool HaloSource(in SurfaceDef s, int quad, int level, int i, int j,
                               out int srcQuad, out int srcI, out int srcJ)
        {
            srcQuad = quad; srcI = i; srcJ = j;
            int  n    = Cells(level);
            bool outI = i < 0 || i > n;
            bool outJ = j < 0 || j > n;
            if (!outI && !outJ) return false;

            // A corner block: the side texel on its South or North side.
            if (outI && outJ) i = math.clamp(i, 0, n);

            Edge edge;
            int  along, depth;
            if (outJ) { edge = j < 0 ? Edge.South : Edge.North; along = i; depth = j < 0 ? -j : j - n; }
            else      { edge = i < 0 ? Edge.West  : Edge.East;  along = j; depth = i < 0 ? -i : i - n; }

            int q = quad, si, sj;
            if (SurfaceMath.QuadEdgeLink(s, quad, edge, out EdgeLink link))
            {
                OnEdge(link.Edge, link.Reversed ? n - along : along, n, out int ei, out int ej);
                Inward(link.Edge, out int di, out int dj);
                q  = link.Face;
                si = math.clamp(ei + depth * di, 0, n);
                sj = math.clamp(ej + depth * dj, 0, n);
            }
            else
            {
                si = math.clamp(i, 0, n);
                sj = math.clamp(j, 0, n);
            }

            EdgeSource(s, q, level, si, sj, out srcQuad, out srcI, out srcJ);
            return true;
        }

        // The step from an edge row into its own quad.
        static void Inward(Edge e, out int di, out int dj)
        {
            switch (e)
            {
                case Edge.South: di = 0;  dj = 1;  return;
                case Edge.North: di = 0;  dj = -1; return;
                case Edge.West:  di = 1;  dj = 0;  return;
                default:         di = -1; dj = 0;  return;   // East
            }
        }

        // Every (destination, source) pair of flat indices the halo rule fills at one level,
        // every border texel of every quad once.
        public int2[] HaloSources(in SurfaceDef s, int level)
        {
            int n    = Cells(level);
            int side = n + 1 + 2 * Border;
            var halo = new int2[QuadCount * (side * side - (n + 1) * (n + 1))];
            int k    = 0;
            for (int q = 0; q < QuadCount; q++)
            for (int j = -Border; j <= n + Border; j++)
            for (int i = -Border; i <= n + Border; i++)
                if (HaloSource(s, q, level, i, j, out int sq, out int si, out int sj))
                    halo[k++] = new int2(Index(q, level, i, j), Index(sq, level, si, sj));
            return halo;
        }
    }
}
