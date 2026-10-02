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
        // Orient once so the ear test only ever has to consider one winding.
        var ring = new List<Vec2>(outer);
        if (SignedArea(ring) < 0)
        {
            ring.Reverse();
        }

        foreach (var hole in holes.OrderByDescending(h => h.Max(p => p.X)))
        {
            var oriented = new List<Vec2>(hole);
            if (SignedArea(oriented) > 0)
            {
                oriented.Reverse(); // Holes wind against the outer ring.
            }

            ring = BridgeHole(ring, oriented);
        }

        var baseIndex = points.Count;
        points.AddRange(ring);

        var clipped = EarClip(ring);
        DelaunayFlip(ring, clipped);

        foreach (var (a, b, c) in clipped)
        {
            triangles.Add((baseIndex + a, baseIndex + b, baseIndex + c));
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
        // Capped because a flip can in principle undo an earlier one on degenerate input; in
        // practice this settles in a handful of passes.
        const int maxPasses = 12;

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

            var flipped = false;
            foreach (var (edge, owners) in edgeOwners)
            {
                if (owners.Second < 0)
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
                flipped = true;
                break; // The owner map is stale now; rebuild it.
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
    private static List<Vec2> BridgeHole(List<Vec2> outer, List<Vec2> hole)
    {
        // Start from the hole's rightmost vertex: a ray cast right from it leaves the hole
        // immediately, so the first outer edge it meets genuinely lies between the two rings.
        var holeIndex = 0;
        for (var i = 1; i < hole.Count; i++)
        {
            if (hole[i].X > hole[holeIndex].X)
            {
                holeIndex = i;
            }
        }

        var start = hole[holeIndex];
        var bridgeIndex = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < outer.Count; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % outer.Count];

            if ((a.Y > start.Y) == (b.Y > start.Y))
            {
                continue;
            }

            var t = (start.Y - a.Y) / (b.Y - a.Y);
            var x = a.X + (t * (b.X - a.X));
            if (x <= start.X)
            {
                continue;
            }

            if (x - start.X < bestDistance)
            {
                bestDistance = x - start.X;
                bridgeIndex = a.X > b.X ? i : (i + 1) % outer.Count;
            }
        }

        if (bridgeIndex < 0)
        {
            // Nothing visible: the hole is not actually inside. Leave the ring untouched rather
            // than stitch in a bridge that would cross it.
            return outer;
        }

        var merged = new List<Vec2>(outer.Count + hole.Count + 2);
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
    private static List<(int A, int B, int C)> EarClip(List<Vec2> ring)
    {
        var triangles = new List<(int, int, int)>();
        if (ring.Count < 3)
        {
            return triangles;
        }

        var indices = Enumerable.Range(0, ring.Count).ToList();

        // Each failed sweep means no ear was found; bail rather than spin on non-simple input.
        var guard = ring.Count * ring.Count;

        while (indices.Count > 3 && guard-- > 0)
        {
            var bestSlot = -1;
            var bestQuality = double.MinValue;

            for (var i = 0; i < indices.Count; i++)
            {
                var previous = indices[(i - 1 + indices.Count) % indices.Count];
                var current = indices[i];
                var next = indices[(i + 1) % indices.Count];

                var a = ring[previous];
                var b = ring[current];
                var c = ring[next];

                if (!IsConvexCorner(a, b, c))
                {
                    continue; // Reflex or collinear: not an ear.
                }

                var contains = false;
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
                        contains = true;
                        break;
                    }
                }

                if (contains)
                {
                    continue;
                }

                // Clipping in index order walks the outline and leaves a fan of slivers behind
                // it; slivers are what become self-intersections once a decal is wrapped.
                var quality = Roundness(a, b, c);
                if (quality > bestQuality)
                {
                    bestQuality = quality;
                    bestSlot = i;
                }
            }

            if (bestSlot < 0)
            {
                // Only collinear or self-touching vertices remain. Fan the remainder: it can add
                // slivers, but never leaves a cap with a hole in it.
                break;
            }

            triangles.Add((
                indices[(bestSlot - 1 + indices.Count) % indices.Count],
                indices[bestSlot],
                indices[(bestSlot + 1) % indices.Count]));
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

    private static bool PointInTriangle(Vec2 p, Vec2 a, Vec2 b, Vec2 c) =>
        Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0;
}
