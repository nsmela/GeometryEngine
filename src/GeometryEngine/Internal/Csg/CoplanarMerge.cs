namespace GeometryEngine.Internal.Csg;

/// <summary>
/// Collapses the redundant triangulation a BSP boolean leaves behind.
///
/// A BSP cuts polygons along infinite planes, so subtracting a cylinder slices the
/// large flat faces it passes through across their whole extent, not just around the
/// bore. The pieces are geometrically correct but there are far more of them than the
/// surface needs, and because every boolean re-splits whatever it is handed, the count
/// snowballs through a chain of operations. Left alone, drilling a handful of holes in
/// a plate turns a few hundred triangles into hundreds of thousands.
///
/// This pass works one supporting plane at a time. The triangles sharing a plane meet
/// along interior edges that each belong to two of them; those edges are artefacts of
/// the cutting and cancel out, leaving only the true outline of the region (its outer
/// boundary and any holes). Re-triangulating that outline restores a minimal
/// tessellation of the same surface.
///
/// Correctness is defended per plane. The re-triangulation is accepted only if it forms
/// a clean manifold patch presenting exactly the boundary the originals did, adds no
/// slivers, and is no larger. If anything about a plane's outline is malformed - a
/// boundary that will not close, a hole that cannot be bridged, an ear clip that
/// overlaps - the merged patch fails one of those checks and the plane keeps its
/// original triangles. Robust triangulation is hard; a robust check is not, so the pass
/// is only ever an optimisation and can never change the surface.
///
/// The merge runs on already-repaired, conforming geometry, so every boundary vertex a
/// neighbour relies on is present and preserved - it introduces no new T-junctions. It
/// pays off on the broad flat faces that fragment worst; curved facets and faces with
/// many interleaved holes fall back cleanly.
/// </summary>
internal static class CoplanarMerge
{
    public static List<int> Merge(IReadOnlyList<Vec3> vertices, List<int> triangles, Tolerance tolerance)
    {
        var groups = GroupByPlane(vertices, triangles, tolerance);
        var result = new List<int>(triangles.Count);

        foreach (var group in groups)
        {
            // A single triangle on a plane has no interior edges to cancel.
            if (group.Triangles.Count == 1)
            {
                CopyTriangle(triangles, group.Triangles[0], result);
                continue;
            }

            var merged = TryMergeGroup(vertices, triangles, group, tolerance);
            if (merged is null)
            {
                foreach (var triangle in group.Triangles)
                {
                    CopyTriangle(triangles, triangle, result);
                }

                continue;
            }

            result.AddRange(merged);
        }

        return result;
    }

    private static void CopyTriangle(List<int> triangles, int start, List<int> into)
    {
        into.Add(triangles[start]);
        into.Add(triangles[start + 1]);
        into.Add(triangles[start + 2]);
    }

    // ---- Grouping by supporting plane ------------------------------------------------

    private readonly record struct PlaneKey(long Nx, long Ny, long Nz, long Offset);

    private sealed class PlaneGroup(Direction normal)
    {
        public Direction Normal { get; } = normal;
        public List<int> Triangles { get; } = [];
    }

    private static List<PlaneGroup> GroupByPlane(IReadOnlyList<Vec3> vertices, List<int> triangles, Tolerance tolerance)
    {
        // Quantise the plane so that triangles that are coplanar to within tolerance
        // land in the same bucket. A cell a little coarser than the tolerance keeps
        // near-identical planes together without merging genuinely distinct ones.
        var quantum = Math.Max(tolerance.Value, 1e-9) * 1000;
        var buckets = new Dictionary<PlaneKey, PlaneGroup>();
        var order = new List<PlaneKey>();

        for (var t = 0; t < triangles.Count; t += 3)
        {
            var a = vertices[triangles[t]];
            var b = vertices[triangles[t + 1]];
            var c = vertices[triangles[t + 2]];

            var maybeNormal = Direction.From((b - a).Cross(c - a));
            if (!maybeNormal.HasValue)
            {
                // A degenerate triangle carries no plane; leave it in a bucket of its
                // own so it is copied through untouched.
                var solo = new PlaneGroup(Direction.Z);
                solo.Triangles.Add(t);
                var soloKey = new PlaneKey(long.MinValue + order.Count, 0, 0, 0);
                buckets[soloKey] = solo;
                order.Add(soloKey);
                continue;
            }

            var normal = maybeNormal.Value;
            var offset = normal.Dot(a);
            var key = new PlaneKey(
                (long)Math.Round(normal.Vector.X / quantum),
                (long)Math.Round(normal.Vector.Y / quantum),
                (long)Math.Round(normal.Vector.Z / quantum),
                (long)Math.Round(offset / quantum));

            if (!buckets.TryGetValue(key, out var group))
            {
                group = new PlaneGroup(normal);
                buckets[key] = group;
                order.Add(key);
            }

            group.Triangles.Add(t);
        }

        var groups = new List<PlaneGroup>(order.Count);
        foreach (var key in order)
        {
            groups.Add(buckets[key]);
        }

        return groups;
    }

    // ---- Merging one plane -----------------------------------------------------------

    private static List<int>? TryMergeGroup(
        IReadOnlyList<Vec3> vertices,
        List<int> triangles,
        PlaneGroup group,
        Tolerance tolerance)
    {
        var boundary = BoundaryEdges(triangles, group.Triangles);
        if (boundary is null || boundary.Count < 3)
        {
            return null;
        }

        var loops = TraceLoops(boundary);
        if (loops is null)
        {
            return null;
        }

        var (u, v) = PlaneBasis(group.Normal);
        var merged = Triangulate(vertices, loops, u, v);
        if (merged is null)
        {
            return null;
        }

        // Merging must not inflate the count.
        if (merged.Count > group.Triangles.Count * 3)
        {
            return null;
        }

        // Nor introduce slivers. The soup arrives free of them, so a near-zero-area
        // triangle here means the outline was too thin for the ear clipper to tile
        // cleanly (the fragments of a curved surface's facets, typically). Decline, and
        // the plane keeps its original, well-shaped triangles. Merging pays off on the
        // broad flat faces that fragment badly, not on slivers, so nothing is lost.
        var sliver = tolerance.Value * tolerance.Value;
        for (var i = 0; i < merged.Count; i += 3)
        {
            if (TriangleArea(vertices, merged[i], merged[i + 1], merged[i + 2]) <= sliver)
            {
                return null;
            }
        }

        // The decisive safety check: the merged triangles must form a clean manifold
        // patch with exactly the boundary the originals presented to the rest of the
        // mesh. A face is stitched to its neighbours along those boundary edges, and
        // its own interior edges must each be shared by exactly two of its triangles;
        // if both hold, the surface cannot spring a leak. A botched triangulation -
        // overlapping ears, a bridge gone wrong - shows up as an edge used more than
        // twice or a boundary that no longer matches, and the plane then keeps its
        // original triangles. Robust triangulation is hard; a robust *check* is not,
        // so the merge is only ever an optimisation and never a risk.
        if (!IsValidPatch(merged, boundary))
        {
            return null;
        }

        return merged;
    }

    /// <summary>
    /// The directed edges that survive after interior edges cancel. Two coplanar
    /// triangles that share an edge traverse it in opposite directions, so those two
    /// half-edges annihilate; an edge on the true boundary is traversed once and
    /// remains. Edges shared with a triangle on a different plane also remain, because
    /// the opposing half-edge is not in this group - which is exactly what keeps the
    /// merged face stitched to its neighbours.
    /// </summary>
    private static Dictionary<int, int>? BoundaryEdges(List<int> triangles, List<int> groupTriangles)
    {
        var count = new Dictionary<(int From, int To), int>();

        foreach (var t in groupTriangles)
        {
            AddHalfEdge(count, triangles[t], triangles[t + 1]);
            AddHalfEdge(count, triangles[t + 1], triangles[t + 2]);
            AddHalfEdge(count, triangles[t + 2], triangles[t]);
        }

        var next = new Dictionary<int, int>();
        foreach (var (edge, uses) in count)
        {
            var opposite = count.GetValueOrDefault((edge.To, edge.From));
            var surviving = uses - opposite;

            // A well-formed boundary leaves each surviving edge exactly once, and never
            // leaves two edges starting at the same vertex. Anything else means the
            // outline is not a clean set of simple loops, so decline the merge.
            if (surviving is < 0 or > 1)
            {
                return null;
            }

            if (surviving == 1)
            {
                if (!next.TryAdd(edge.From, edge.To))
                {
                    return null;
                }
            }
        }

        return next;
    }

    private static void AddHalfEdge(Dictionary<(int, int), int> count, int from, int to) =>
        count[(from, to)] = count.GetValueOrDefault((from, to)) + 1;

    /// <summary>Walks the surviving half-edges into closed loops of vertex indices.</summary>
    private static List<List<int>>? TraceLoops(Dictionary<int, int> next)
    {
        var loops = new List<List<int>>();
        var visited = new HashSet<int>();

        foreach (var start in next.Keys)
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var loop = new List<int> { start };
            var current = next[start];

            while (current != start)
            {
                if (!visited.Add(current) || !next.TryGetValue(current, out var following))
                {
                    return null;
                }

                loop.Add(current);
                current = following;
            }

            if (loop.Count < 3)
            {
                return null;
            }

            loops.Add(loop);
        }

        return loops.Count == 0 ? null : loops;
    }

    // ---- 2D triangulation of the outline --------------------------------------------

    private static (Vec3 U, Vec3 V) PlaneBasis(Direction normal)
    {
        // Any unit vector not parallel to the normal seeds an in-plane basis. The
        // right-handed (u, v, n) frame makes a 2D counter-clockwise winding correspond
        // to the outward normal, so the re-triangulated faces keep their orientation.
        var n = normal.Vector;
        var seed = Math.Abs(n.X) <= Math.Abs(n.Y) && Math.Abs(n.X) <= Math.Abs(n.Z)
            ? Vec3.UnitX
            : Math.Abs(n.Y) <= Math.Abs(n.Z) ? Vec3.UnitY : Vec3.UnitZ;

        var u = Direction.From(seed.Cross(n)).GetValueOrDefault(Direction.X).Vector;
        var v = n.Cross(u);
        return (u, v);
    }

    private readonly record struct Point2(double X, double Y, int Index);

    private static List<int>? Triangulate(IReadOnlyList<Vec3> vertices, List<List<int>> loops, Vec3 u, Vec3 v)
    {
        var rings = new List<(List<Point2> Points, double Area)>(loops.Count);
        foreach (var loop in loops)
        {
            var points = new List<Point2>(loop.Count);
            foreach (var index in loop)
            {
                var p = vertices[index];
                points.Add(new Point2(p.Dot(u), p.Dot(v), index));
            }

            rings.Add((points, SignedArea(points)));
        }

        // Outer boundaries wind counter-clockwise (positive area); holes wind the other
        // way. Match every hole to the outer ring that contains it.
        var outers = new List<(List<Point2> Points, List<List<Point2>> Holes)>();
        foreach (var ring in rings)
        {
            if (ring.Area > 0)
            {
                outers.Add((ring.Points, []));
            }
        }

        if (outers.Count == 0)
        {
            return null;
        }

        foreach (var ring in rings)
        {
            if (ring.Area >= 0)
            {
                continue;
            }

            var host = FindContainingOuter(outers, ring.Points[0]);
            if (host < 0)
            {
                return null;
            }

            outers[host].Holes.Add(ring.Points);
        }

        var result = new List<int>();
        foreach (var (outline, holes) in outers)
        {
            var polygon = holes.Count == 0 ? outline : BridgeHoles(outline, holes);
            if (polygon is null)
            {
                return null;
            }

            if (!EarClip(polygon, result))
            {
                return null;
            }
        }

        return result;
    }

    private static double SignedArea(List<Point2> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum * 0.5;
    }

    private static int FindContainingOuter(
        List<(List<Point2> Points, List<List<Point2>> Holes)> outers,
        Point2 probe)
    {
        for (var i = 0; i < outers.Count; i++)
        {
            if (Contains(outers[i].Points, probe))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Contains(List<Point2> polygon, Point2 probe)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (pi.Y > probe.Y != pj.Y > probe.Y &&
                probe.X < ((pj.X - pi.X) * (probe.Y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Cuts each hole into the outer ring so the whole outline becomes one simple loop.
    /// A hole is joined at its right-most vertex to a visible vertex of the outer ring;
    /// the bridge is walked out along the hole and back, which leaves the loop simple.
    /// </summary>
    private static List<Point2>? BridgeHoles(List<Point2> outer, List<List<Point2>> holes)
    {
        var ordered = holes
            .Select(hole => (Hole: hole, MaxX: hole.Max(p => p.X)))
            .OrderByDescending(entry => entry.MaxX)
            .Select(entry => entry.Hole);

        var polygon = new List<Point2>(outer);

        foreach (var hole in ordered)
        {
            var bridgeHole = ArgMaxX(hole);
            var bridgeOuter = FindVisibleVertex(polygon, hole[bridgeHole]);
            if (bridgeOuter < 0)
            {
                return null;
            }

            var stitched = new List<Point2>(polygon.Count + hole.Count + 2);
            for (var i = 0; i <= bridgeOuter; i++)
            {
                stitched.Add(polygon[i]);
            }

            for (var i = 0; i < hole.Count; i++)
            {
                stitched.Add(hole[(bridgeHole + i) % hole.Count]);
            }

            stitched.Add(hole[bridgeHole]);
            for (var i = bridgeOuter; i < polygon.Count; i++)
            {
                stitched.Add(polygon[i]);
            }

            polygon = stitched;
        }

        return polygon;
    }

    private static int ArgMaxX(List<Point2> points)
    {
        var best = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].X > points[best].X)
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The outer vertex a hole should bridge to: cast a ray in +x from the hole's
    /// right-most point, land on the nearest outer edge, and take the visible endpoint.
    /// Good enough for the convex-ish outlines CSG produces; a failure returns -1 and
    /// the plane keeps its original triangles.
    /// </summary>
    private static int FindVisibleVertex(List<Point2> polygon, Point2 from)
    {
        var bestEdge = -1;
        var bestX = double.MaxValue;

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];

            // Only edges the rightward ray can cross.
            if (a.Y > from.Y == b.Y > from.Y)
            {
                continue;
            }

            var tParam = (from.Y - a.Y) / (b.Y - a.Y);
            var crossX = a.X + (tParam * (b.X - a.X));
            if (crossX >= from.X && crossX < bestX)
            {
                bestX = crossX;
                bestEdge = i;
            }
        }

        if (bestEdge < 0)
        {
            return -1;
        }

        // The endpoint with the larger x is the candidate the ray "sees" first.
        var end = (bestEdge + 1) % polygon.Count;
        return polygon[bestEdge].X >= polygon[end].X ? bestEdge : end;
    }

    /// <summary>Ear clipping for a simple polygon wound counter-clockwise.</summary>
    private static bool EarClip(List<Point2> polygon, List<int> into)
    {
        var remaining = new List<Point2>(polygon);
        if (remaining.Count < 3)
        {
            return false;
        }

        var guard = 0;
        var limit = remaining.Count * remaining.Count;

        while (remaining.Count > 3)
        {
            if (guard++ > limit)
            {
                return false;
            }

            var clipped = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var prev = remaining[(i - 1 + remaining.Count) % remaining.Count];
                var current = remaining[i];
                var nextPoint = remaining[(i + 1) % remaining.Count];

                if (!IsConvex(prev, current, nextPoint) || !IsEar(remaining, i, prev, current, nextPoint))
                {
                    continue;
                }

                into.Add(prev.Index);
                into.Add(current.Index);
                into.Add(nextPoint.Index);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                return false;
            }
        }

        into.Add(remaining[0].Index);
        into.Add(remaining[1].Index);
        into.Add(remaining[2].Index);
        return true;
    }

    private static bool IsConvex(Point2 a, Point2 b, Point2 c) => Cross(a, b, c) > 0;

    private static bool IsEar(List<Point2> polygon, int tip, Point2 a, Point2 b, Point2 c)
    {
        for (var i = 0; i < polygon.Count; i++)
        {
            if (i == tip || polygon[i].Index == a.Index || polygon[i].Index == b.Index || polygon[i].Index == c.Index)
            {
                continue;
            }

            if (InTriangle(polygon[i], a, b, c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InTriangle(Point2 p, Point2 a, Point2 b, Point2 c)
    {
        var d1 = Cross(a, b, p);
        var d2 = Cross(b, c, p);
        var d3 = Cross(c, a, p);
        var hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPositive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNegative && hasPositive);
    }

    private static double Cross(Point2 a, Point2 b, Point2 c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    /// <summary>
    /// True when the merged triangles form a clean manifold patch whose boundary is
    /// exactly the one the original group presented: no triangle is degenerate, every
    /// undirected edge is used at most twice, and the half-edges that survive once are
    /// precisely the expected boundary. Overlaps push an edge past two uses; gaps turn
    /// an interior edge into an unexpected boundary; both are rejected.
    /// </summary>
    private static bool IsValidPatch(List<int> merged, Dictionary<int, int> expected)
    {
        var directed = new Dictionary<(int, int), int>();
        var undirected = new Dictionary<(int, int), int>();

        for (var i = 0; i < merged.Count; i += 3)
        {
            var a = merged[i];
            var b = merged[i + 1];
            var c = merged[i + 2];

            if (a == b || b == c || c == a)
            {
                return false;
            }

            AddHalfEdge(directed, a, b);
            AddHalfEdge(directed, b, c);
            AddHalfEdge(directed, c, a);
            AddUndirected(undirected, a, b);
            AddUndirected(undirected, b, c);
            AddUndirected(undirected, c, a);
        }

        foreach (var uses in undirected.Values)
        {
            if (uses > 2)
            {
                return false;
            }
        }

        var surviving = new Dictionary<int, int>();
        foreach (var (edge, uses) in directed)
        {
            var net = uses - directed.GetValueOrDefault((edge.Item2, edge.Item1));
            if (net is < 0 or > 1)
            {
                return false;
            }

            if (net == 1 && !surviving.TryAdd(edge.Item1, edge.Item2))
            {
                return false;
            }
        }

        if (surviving.Count != expected.Count)
        {
            return false;
        }

        foreach (var (from, to) in expected)
        {
            if (!surviving.TryGetValue(from, out var actual) || actual != to)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddUndirected(Dictionary<(int, int), int> count, int p, int q)
    {
        var key = p < q ? (p, q) : (q, p);
        count[key] = count.GetValueOrDefault(key) + 1;
    }

    private static double TriangleArea(IReadOnlyList<Vec3> vertices, int a, int b, int c) =>
        (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]).Length * 0.5;
}
