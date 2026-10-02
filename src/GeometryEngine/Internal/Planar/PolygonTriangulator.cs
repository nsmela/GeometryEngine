namespace GeometryEngine.Internal.Planar;

/// <summary>
/// Triangulates closed planar contours, holes included. The extrusion and decal slices need the
/// triangles themselves - they lift every vertex to its own height - so the native kernel's
/// extrude, which triangulates internally and hands back only a finished solid, cannot stand in.
/// </summary>
internal static class PolygonTriangulator
{
    /// <summary>Below this a signed area is noise rather than a contour.</summary>
    private const double MinContourArea = 1e-12;

    /// <summary>
    /// Triangulates the given contours. Outer contours and holes are told apart by containment,
    /// not by the order they arrive in or their winding. Triangles come back counter-clockwise.
    /// </summary>
    public static (List<Vec2> Points, List<(int A, int B, int C)> Triangles) Triangulate(
        IReadOnlyList<IReadOnlyList<Vec2>> contours)
    {
        var points = new List<Vec2>();
        var triangles = new List<(int, int, int)>();

        foreach (var (outer, holes) in Nest(contours))
        {
            TriangulateWithHoles(outer, holes, points, triangles);
        }

        return (points, triangles);
    }

    /// <summary>
    /// Groups contours into outlines and the holes directly inside each, by containment: a ring
    /// nested inside an odd number of others is a hole, and an island inside a hole is an outline
    /// of its own. Order and winding are ignored, and both come back as they arrived. Repeated
    /// points are dropped, and so are rings with fewer than three points or no area.
    /// </summary>
    public static List<(List<Vec2> Outer, List<List<Vec2>> Holes)> Nest(IReadOnlyList<IReadOnlyList<Vec2>> contours)
    {
        var nested = new List<(List<Vec2>, List<List<Vec2>>)>();

        var rings = new List<List<Vec2>>();
        foreach (var contour in contours)
        {
            var ring = Clean(contour);
            if (ring.Count >= 3 && Math.Abs(SignedArea(ring)) > MinContourArea)
            {
                rings.Add(ring);
            }
        }

        if (rings.Count == 0)
        {
            return nested;
        }

        // A ring nested inside an odd number of other rings is a hole.
        var isHole = new bool[rings.Count];
        for (var i = 0; i < rings.Count; i++)
        {
            var depth = 0;
            for (var j = 0; j < rings.Count; j++)
            {
                if (i != j && Contains(rings[j], rings[i][0]))
                {
                    depth++;
                }
            }

            isHole[i] = (depth & 1) == 1;
        }

        for (var i = 0; i < rings.Count; i++)
        {
            if (isHole[i])
            {
                continue;
            }

            var holes = new List<List<Vec2>>();
            for (var j = 0; j < rings.Count; j++)
            {
                // Only holes directly inside this ring; a nested island's own holes are its own.
                if (!isHole[j] || !Contains(rings[i], rings[j][0]))
                {
                    continue;
                }

                var nestedDeeper = false;
                for (var k = 0; k < rings.Count; k++)
                {
                    if (k != i && !isHole[k] && Contains(rings[i], rings[k][0]) && Contains(rings[k], rings[j][0]))
                    {
                        nestedDeeper = true;
                        break;
                    }
                }

                if (!nestedDeeper)
                {
                    holes.Add(rings[j]);
                }
            }

            nested.Add((rings[i], holes));
        }

        return nested;
    }

    private static void TriangulateWithHoles(
        List<Vec2> outer,
        List<List<Vec2>> holes,
        List<Vec2> points,
        List<(int, int, int)> triangles)
    {
        // The ring is kept as ids into one list of distinct points. Bridging a hole visits the
        // bridge's two ends twice, and both visits must stay the same point: as two points, every
        // edge meeting a bridge end has differently numbered copies on its two sides, and the
        // prism builder takes each such edge for an outline and stands a wall on it - inside the
        // letter, from its edge to the counter.
        var distinct = new List<Vec2>(outer);

        // Orient once so the ear test only ever has to consider one winding.
        if (SignedArea(distinct) < 0)
        {
            distinct.Reverse();
        }

        var ring = Enumerable.Range(0, distinct.Count).ToList();

        foreach (var hole in holes.OrderByDescending(h => h.Max(p => p.X)))
        {
            var oriented = new List<Vec2>(hole);
            if (SignedArea(oriented) > 0)
            {
                oriented.Reverse(); // Holes wind against the outer ring.
            }

            var holeIds = Enumerable.Range(distinct.Count, oriented.Count).ToList();
            distinct.AddRange(oriented);
            ring = BridgeHole(ring, holeIds, distinct);
        }

        var positions = ring.Select(id => distinct[id]).ToList();
        var clipped = new List<(int A, int B, int C)>();
        foreach (var (a, b, c) in EarClip(positions))
        {
            var (ia, ib, ic) = (ring[a], ring[b], ring[c]);

            // Two corners on the same bridge end make a triangle with no area.
            if (ia != ib && ib != ic && ic != ia)
            {
                clipped.Add((ia, ib, ic));
            }
        }

        DelaunayFlip(distinct, clipped);

        // Only points a triangle uses: a hole that could not be bridged contributes none.
        var used = new int[distinct.Count];
        Array.Fill(used, -1);
        foreach (var (a, b, c) in clipped)
        {
            used[a] = used[b] = used[c] = 0;
        }

        for (var id = 0; id < distinct.Count; id++)
        {
            if (used[id] == 0)
            {
                used[id] = points.Count;
                points.Add(distinct[id]);
            }
        }

        foreach (var (a, b, c) in clipped)
        {
            triangles.Add((used[a], used[b], used[c]));
        }
    }

    /// <summary>
    /// Flips shared edges until no triangle's circumcircle contains the opposite vertex, turning
    /// the ear-clipped fan into a Delaunay triangulation.
    ///
    /// This matters more than it looks. Ear clipping is free to emit slivers spanning a whole
    /// outline, and the decal slice lifts each vertex onto a curved surface independently - so a
    /// sliver reaching across a wrapped label cuts straight through the geometry beside it, and
    /// the prism comes back self-intersecting.
    /// </summary>
    private static void DelaunayFlip(List<Vec2> ring, List<(int A, int B, int C)> triangles)
    {
        // Each pass flips every edge that still fails the test and whose two triangles have not
        // already been changed this pass - their owner entries would be stale - then the owner
        // map is rebuilt for the next. Flipping only one edge per rebuild, under a fixed cap of
        // passes, used to stop after a dozen flips however many slivers remained. The cap here
        // only guards against a cycle on degenerate input; each pass that flips anything brings
        // the triangulation closer to Delaunay, and in practice it settles in a handful.
        var maxPasses = Math.Max(12, triangles.Count);
        var touched = new bool[triangles.Count];

        for (var pass = 0; pass < maxPasses; pass++)
        {
            var edgeOwners = new Dictionary<(int, int), (int First, int Second)>();
            for (var t = 0; t < triangles.Count; t++)
            {
                var (a, b, c) = triangles[t];
                Register(edgeOwners, a, b, t);
                Register(edgeOwners, b, c, t);
                Register(edgeOwners, c, a, t);
            }

            Array.Clear(touched);
            var flipped = false;
            foreach (var (edge, owners) in edgeOwners)
            {
                if (owners.Second < 0 || touched[owners.First] || touched[owners.Second])
                {
                    continue;
                }

                var opposite0 = Opposite(triangles[owners.First], edge);
                var opposite1 = Opposite(triangles[owners.Second], edge);
                if (opposite0 < 0 || opposite1 < 0)
                {
                    continue;
                }

                var p = ring[edge.Item1];
                var q = ring[edge.Item2];
                var r = ring[opposite0];
                var s = ring[opposite1];

                if (!InCircumcircle(p, q, r, s))
                {
                    continue;
                }

                // The flipped quad must stay convex, or the swap would fold the pair over.
                if (Cross(r, p, s) <= 0 || Cross(r, s, q) <= 0)
                {
                    continue;
                }

                triangles[owners.First] = (opposite0, edge.Item1, opposite1);
                triangles[owners.Second] = (opposite1, edge.Item2, opposite0);
                touched[owners.First] = true;
                touched[owners.Second] = true;
                flipped = true;
            }

            if (!flipped)
            {
                return;
            }
        }
    }

    private static void Register(Dictionary<(int, int), (int First, int Second)> owners, int a, int b, int triangle)
    {
        var key = a < b ? (a, b) : (b, a);
        if (owners.TryGetValue(key, out var existing))
        {
            if (existing.Second < 0)
            {
                owners[key] = (existing.First, triangle);
            }
        }
        else
        {
            owners[key] = (triangle, -1);
        }
    }

    private static int Opposite((int A, int B, int C) triangle, (int, int) edge)
    {
        if (triangle.A != edge.Item1 && triangle.A != edge.Item2)
        {
            return triangle.A;
        }

        if (triangle.B != edge.Item1 && triangle.B != edge.Item2)
        {
            return triangle.B;
        }

        return triangle.C != edge.Item1 && triangle.C != edge.Item2 ? triangle.C : -1;
    }

    private static bool InCircumcircle(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        // The standard determinant test, which only holds for a counter-clockwise a-b-c.
        if (Cross(a, b, c) < 0)
        {
            (b, c) = (c, b);
        }

        double ax = a.X - d.X, ay = a.Y - d.Y;
        double bx = b.X - d.X, by = b.Y - d.Y;
        double cx = c.X - d.X, cy = c.Y - d.Y;

        var determinant =
            (((ax * ax) + (ay * ay)) * ((bx * cy) - (cx * by))) -
            (((bx * bx) + (by * by)) * ((ax * cy) - (cx * ay))) +
            (((cx * cx) + (cy * cy)) * ((ax * by) - (bx * ay)));

        return determinant > 1e-12;
    }

    /// <summary>
    /// Cuts a hole into its containing ring along a mutually visible pair of vertices, turning the
    /// two loops into one. The doubled-back bridge edge is what makes the result ear-clippable.
    /// </summary>
    /// <remarks>
    /// Both rings, and the result, are ids into <paramref name="points"/>. This is Eberly's
    /// construction, and both of its checks matter. The edge the ray meets first has an end in
    /// view of the hole only when no reflex corner of the ring stands in between. And once one
    /// hole is bridged, the vertex it was bridged to is visited twice: the second hole has to be
    /// joined at the visit facing it, or the ring crosses itself and the clipped triangles
    /// overlap - two counters bridged to the same corner of a B did exactly that.
    /// </remarks>
    private static List<int> BridgeHole(List<int> outer, List<int> hole, List<Vec2> points)
    {
        // Start from the hole's rightmost vertex: a ray cast right from it leaves the hole
        // immediately, so the first outer edge it meets genuinely lies between the two rings.
        var holeIndex = 0;
        for (var i = 1; i < hole.Count; i++)
        {
            if (points[hole[i]].X > points[hole[holeIndex]].X)
            {
                holeIndex = i;
            }
        }

        var start = points[hole[holeIndex]];
        var edge = -1;
        var hitX = double.MaxValue;

        for (var i = 0; i < outer.Count; i++)
        {
            var a = points[outer[i]];
            var b = points[outer[(i + 1) % outer.Count]];

            if ((a.Y > start.Y) == (b.Y > start.Y))
            {
                continue;
            }

            var t = (start.Y - a.Y) / (b.Y - a.Y);
            var x = a.X + (t * (b.X - a.X));
            if (x > start.X && x < hitX)
            {
                hitX = x;
                edge = i;
            }
        }

        if (edge < 0)
        {
            // Nothing visible: the hole is not actually inside. Leave the ring untouched rather
            // than stitch in a bridge that would cross it.
            return outer;
        }

        var hit = new Vec2(hitX, start.Y);
        var first = outer[edge];
        var second = outer[(edge + 1) % outer.Count];
        var target = points[first].X > points[second].X ? first : second;

        // Unless the ray landed on the vertex itself, a reflex corner inside the triangle between
        // the hole, the hit and that vertex hides it. The one nearest the ray's direction is in view.
        if (points[target] != hit)
        {
            var candidate = points[target];
            var bestAngle = double.MaxValue;
            var bestDistance = double.MaxValue;

            for (var i = 0; i < outer.Count; i++)
            {
                var point = points[outer[i]];
                if (point == candidate)
                {
                    continue;
                }

                var previous = points[outer[(i - 1 + outer.Count) % outer.Count]];
                var next = points[outer[(i + 1) % outer.Count]];
                if (Cross(previous, point, next) >= 0 || !InTriangle(point, start, hit, candidate))
                {
                    continue;
                }

                var angle = Math.Abs(Math.Atan2(point.Y - start.Y, point.X - start.X));
                var distance = (point - start).LengthSquared;
                if (angle < bestAngle || (angle == bestAngle && distance < bestDistance))
                {
                    bestAngle = angle;
                    bestDistance = distance;
                    target = outer[i];
                }
            }
        }

        var bridgeIndex = -1;
        for (var i = 0; i < outer.Count && bridgeIndex < 0; i++)
        {
            if (outer[i] == target &&
                InWedge(points[outer[(i - 1 + outer.Count) % outer.Count]], points[target], points[outer[(i + 1) % outer.Count]], start))
            {
                bridgeIndex = i;
            }
        }

        if (bridgeIndex < 0)
        {
            bridgeIndex = outer.IndexOf(target);
        }

        var merged = new List<int>(outer.Count + hole.Count + 2);
        for (var i = 0; i <= bridgeIndex; i++)
        {
            merged.Add(outer[i]);
        }

        for (var i = 0; i < hole.Count; i++)
        {
            merged.Add(hole[(holeIndex + i) % hole.Count]);
        }

        merged.Add(hole[holeIndex]);
        for (var i = bridgeIndex; i < outer.Count; i++)
        {
            merged.Add(outer[i]);
        }

        return merged;
    }

    /// <summary>Ear clipping on a counter-clockwise simple polygon, taking the roundest ear each time.</summary>
    /// <remarks>
    /// Each corner's ear test is kept between clips rather than repeated for every corner after
    /// every clip, which made a 2,000-point outline take ten seconds. Clipping a corner changes
    /// only two things: the triangles at its two neighbours, and - since the clipped point is gone
    /// - whether it still blocks any corner it lay inside. Nothing can become blocked, because no
    /// point is added. So only the neighbours, and the corners the clipped point was blocking,
    /// are tested again; every other answer still holds, and the ear chosen each round is the one
    /// a full rescan would choose.
    /// </remarks>
    private static List<(int A, int B, int C)> EarClip(List<Vec2> ring)
    {
        var triangles = new List<(int, int, int)>();
        if (ring.Count < 3)
        {
            return triangles;
        }

        var indices = Enumerable.Range(0, ring.Count).ToList();

        // Per corner, by its index into the ring: whether its test is still current, whether it is
        // an ear and how round, and - for a convex corner that is not - the point inside it.
        var tested = new bool[ring.Count];
        var isEar = new bool[ring.Count];
        var quality = new double[ring.Count];
        var blocker = new int[ring.Count];
        var clipped = new bool[ring.Count];

        // Each failed sweep means no ear was found; bail rather than spin on non-simple input.
        var guard = ring.Count * ring.Count;

        while (indices.Count > 3 && guard-- > 0)
        {
            var bestSlot = -1;
            var bestQuality = double.MinValue;

            for (var i = 0; i < indices.Count; i++)
            {
                var current = indices[i];
                if (!tested[current] || (blocker[current] >= 0 && clipped[blocker[current]]))
                {
                    TestEar(ring, indices, i, isEar, quality, blocker);
                    tested[current] = true;
                }

                if (isEar[current] && quality[current] > bestQuality)
                {
                    bestQuality = quality[current];
                    bestSlot = i;
                }
            }

            if (bestSlot < 0)
            {
                // Only collinear or self-touching vertices remain. Fan the remainder: it can add
                // slivers, but never leaves a cap with a hole in it.
                break;
            }

            var previous = indices[(bestSlot - 1 + indices.Count) % indices.Count];
            var next = indices[(bestSlot + 1) % indices.Count];
            triangles.Add((previous, indices[bestSlot], next));

            clipped[indices[bestSlot]] = true;
            tested[previous] = false;
            tested[next] = false;
            indices.RemoveAt(bestSlot);
        }

        if (indices.Count == 3)
        {
            triangles.Add((indices[0], indices[1], indices[2]));
        }
        else if (indices.Count > 3)
        {
            for (var i = 1; i < indices.Count - 1; i++)
            {
                triangles.Add((indices[0], indices[i], indices[i + 1]));
            }
        }

        return triangles;
    }

    /// <summary>Tests the corner at <paramref name="slot"/> of the remaining outline, recording the answer by its ring index.</summary>
    private static void TestEar(List<Vec2> ring, List<int> indices, int slot, bool[] isEar, double[] quality, int[] blocker)
    {
        var previous = indices[(slot - 1 + indices.Count) % indices.Count];
        var current = indices[slot];
        var next = indices[(slot + 1) % indices.Count];

        var a = ring[previous];
        var b = ring[current];
        var c = ring[next];

        isEar[current] = false;
        blocker[current] = -1;

        if (!IsConvexCorner(a, b, c))
        {
            return; // Reflex or collinear: not an ear, until a neighbour changes.
        }

        foreach (var other in indices)
        {
            if (other == previous || other == current || other == next)
            {
                continue;
            }

            // A hole's bridge visits two positions twice, so another index can sit exactly
            // on this ear's corner. That copy is the same point, not a point inside the
            // ear; counting it would block every ear along the bridge and leave the rest
            // to the fan below.
            var point = ring[other];
            if (point == a || point == b || point == c)
            {
                continue;
            }

            if (PointInTriangle(point, a, b, c))
            {
                blocker[current] = other;
                return;
            }
        }

        // Clipping in index order walks the outline and leaves a fan of slivers behind
        // it; slivers are what become self-intersections once a decal is wrapped.
        isEar[current] = true;
        quality[current] = Roundness(a, b, c);
    }

    private static List<Vec2> Clean(IReadOnlyList<Vec2> contour)
    {
        const double epsilonSquared = 1e-20;
        var cleaned = new List<Vec2>(contour.Count);

        foreach (var point in contour)
        {
            if (cleaned.Count == 0 || (cleaned[^1] - point).LengthSquared >= epsilonSquared)
            {
                cleaned.Add(point);
            }
        }

        while (cleaned.Count > 1 && (cleaned[0] - cleaned[^1]).LengthSquared < epsilonSquared)
        {
            cleaned.RemoveAt(cleaned.Count - 1);
        }

        return cleaned;
    }

    public static double SignedArea(IReadOnlyList<Vec2> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            sum += ring[i].Cross(ring[(i + 1) % ring.Count]);
        }

        return sum * 0.5;
    }

    public static bool Contains(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Area over the sum of squared sides: peaks for an equilateral triangle, tends to zero for a sliver.</summary>
    private static double Roundness(Vec2 a, Vec2 b, Vec2 c)
    {
        var sumOfSquares = (b - a).LengthSquared + (c - b).LengthSquared + (a - c).LengthSquared;
        return sumOfSquares < 1e-30 ? 0 : Cross(a, b, c) / sumOfSquares;
    }

    private static double Cross(Vec2 a, Vec2 b, Vec2 c) => (b - a).Cross(c - a);

    /// <summary>
    /// True when the corner at <paramref name="b"/> turns left by more than rounding noise.
    ///
    /// The test is on the sine of the turn, not the sign of the cross product. An outline
    /// subdivided so it can bend over a curved surface puts runs of exactly collinear points
    /// along each straight stroke, and rounding leaves their cross product a hair either side of
    /// zero. Taken at its sign, three such points are an ear, and clipping it leaves a triangle
    /// with no area in the cap - found on the diagonal of a bold Z.
    /// </summary>
    private static bool IsConvexCorner(Vec2 a, Vec2 b, Vec2 c)
    {
        const double minimumSine = 1e-7;
        var lengths = Math.Sqrt((b - a).LengthSquared * (c - b).LengthSquared);
        return Cross(a, b, c) > minimumSine * lengths;
    }

    /// <summary>Whether <paramref name="p"/> is inside or on a triangle of either winding.</summary>
    private static bool InTriangle(Vec2 p, Vec2 a, Vec2 b, Vec2 c)
    {
        var ab = Cross(a, b, p);
        var bc = Cross(b, c, p);
        var ca = Cross(c, a, p);
        return (ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0);
    }

    /// <summary>
    /// Whether <paramref name="target"/> lies inside the corner a counter-clockwise ring makes at
    /// <paramref name="corner"/>, which sweeps counter-clockwise from the way to
    /// <paramref name="next"/> round to the way to <paramref name="previous"/>.
    /// </summary>
    private static bool InWedge(Vec2 previous, Vec2 corner, Vec2 next, Vec2 target)
    {
        var toNext = next - corner;
        var toPrevious = previous - corner;
        var toTarget = target - corner;

        if (Cross(previous, corner, next) >= 0)
        {
            return toNext.Cross(toTarget) > 0 && toTarget.Cross(toPrevious) > 0;
        }

        // A reflex corner: inside unless the target is in the narrow outside wedge.
        return !(toPrevious.Cross(toTarget) > 0 && toTarget.Cross(toNext) > 0);
    }

    private static bool PointInTriangle(Vec2 p, Vec2 a, Vec2 b, Vec2 c) =>
        Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0;
}
