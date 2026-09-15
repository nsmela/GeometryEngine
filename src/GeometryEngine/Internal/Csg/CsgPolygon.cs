namespace GeometryEngine.Internal.Csg;

/// <summary>
/// A convex, planar polygon. Every polygon in the kernel starts life as a mesh
/// triangle and is only ever cut by planes, and cutting a convex polygon with a
/// plane yields convex pieces, so convexity is an invariant of the whole pipeline.
/// The polygon keeps the plane it was born with: recomputing a plane from a thin
/// fragment is numerically unreliable and can silently flip an orientation.
/// </summary>
internal sealed record CsgPolygon
{
    public ImmutableArray<Vec3> Vertices { get; }
    public Plane Plane { get; }

    private CsgPolygon(ImmutableArray<Vec3> vertices, Plane plane)
    {
        Vertices = vertices;
        Plane = plane;
    }

    /// <summary>
    /// Builds a polygon and derives its plane with Newell's method, which averages
    /// over every edge instead of trusting any three vertices.
    /// Returns None when the loop is degenerate.
    /// </summary>
    public static Maybe<CsgPolygon> FromVertices(ImmutableArray<Vec3> vertices)
    {
        if (vertices.Length < 3)
        {
            return Maybe<CsgPolygon>.None();
        }

        var normal = Vec3.Zero;
        var centroid = Vec3.Zero;
        for (var i = 0; i < vertices.Length; i++)
        {
            var current = vertices[i];
            var next = vertices[(i + 1) % vertices.Length];
            normal += new Vec3(
                (current.Y - next.Y) * (current.Z + next.Z),
                (current.Z - next.Z) * (current.X + next.X),
                (current.X - next.X) * (current.Y + next.Y));
            centroid += current;
        }

        centroid /= vertices.Length;

        return Direction.From(normal)
            .Map(direction => new CsgPolygon(vertices, Plane.FromNormalAndPoint(direction, centroid)));
    }

    /// <summary>Wraps a vertex loop that is known to lie on an already-computed plane.</summary>
    public static CsgPolygon OnPlane(ImmutableArray<Vec3> vertices, Plane plane) => new(vertices, plane);

    /// <summary>The same surface facing the other way.</summary>
    public CsgPolygon Flipped()
    {
        var reversed = ImmutableArray.CreateBuilder<Vec3>(Vertices.Length);
        for (var i = Vertices.Length - 1; i >= 0; i--)
        {
            reversed.Add(Vertices[i]);
        }

        return new CsgPolygon(reversed.MoveToImmutable(), Plane.Flipped());
    }
}

/// <summary>The four buckets a polygon can fall into when a plane is passed through it.</summary>
internal sealed record PolygonSplit(
    IReadOnlyList<CsgPolygon> CoplanarFront,
    IReadOnlyList<CsgPolygon> CoplanarBack,
    IReadOnlyList<CsgPolygon> Front,
    IReadOnlyList<CsgPolygon> Back);

/// <summary>
/// Cuts polygons with a plane. This is the one place in the library where floating
/// point classification happens, so the tolerance enters the algorithm exactly once.
/// </summary>
internal static class PolygonSplitter
{
    public static void Split(
        Plane divider,
        CsgPolygon polygon,
        Tolerance tolerance,
        List<CsgPolygon> coplanarFront,
        List<CsgPolygon> coplanarBack,
        List<CsgPolygon> front,
        List<CsgPolygon> back)
    {
        var vertices = polygon.Vertices;
        Span<PointSide> sides = vertices.Length <= 32 ? stackalloc PointSide[vertices.Length] : new PointSide[vertices.Length];

        var combined = PointSide.Coplanar;
        for (var i = 0; i < vertices.Length; i++)
        {
            sides[i] = divider.Classify(vertices[i], tolerance);
            combined |= sides[i];
        }

        switch (combined)
        {
            case PointSide.Coplanar:
                if (divider.Normal.Dot(polygon.Plane.Normal.Vector) > 0)
                {
                    coplanarFront.Add(polygon);
                }
                else
                {
                    coplanarBack.Add(polygon);
                }

                break;

            case PointSide.Front:
                front.Add(polygon);
                break;

            case PointSide.Back:
                back.Add(polygon);
                break;

            default:
                SplitSpanning(divider, polygon, sides, front, back);
                break;
        }
    }

    /// <summary>Walks the loop once, emitting a front piece and a back piece.</summary>
    private static void SplitSpanning(
        Plane divider,
        CsgPolygon polygon,
        ReadOnlySpan<PointSide> sides,
        List<CsgPolygon> front,
        List<CsgPolygon> back)
    {
        var vertices = polygon.Vertices;
        var frontLoop = ImmutableArray.CreateBuilder<Vec3>();
        var backLoop = ImmutableArray.CreateBuilder<Vec3>();

        for (var i = 0; i < vertices.Length; i++)
        {
            var j = (i + 1) % vertices.Length;
            var current = vertices[i];

            if (sides[i] != PointSide.Back)
            {
                frontLoop.Add(current);
            }

            if (sides[i] != PointSide.Front)
            {
                backLoop.Add(current);
            }

            if ((sides[i] | sides[j]) != (PointSide.Front | PointSide.Back))
            {
                continue;
            }

            // The edge crosses the plane; both pieces gain the same crossing point.
            var crossing = divider.IntersectSegment(current, vertices[j]);
            frontLoop.Add(crossing);
            backLoop.Add(crossing);
        }

        if (frontLoop.Count >= 3)
        {
            front.Add(CsgPolygon.OnPlane(frontLoop.ToImmutable(), polygon.Plane));
        }

        if (backLoop.Count >= 3)
        {
            back.Add(CsgPolygon.OnPlane(backLoop.ToImmutable(), polygon.Plane));
        }
    }
}
