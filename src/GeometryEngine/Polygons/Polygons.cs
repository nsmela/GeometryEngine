using Clipper2Lib;
using GeometryEngine.Internal.Native;
using GeometryEngine.Internal.Planar;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Algorithm.Hull;
using NetTopologySuite.Geometries;

namespace GeometryEngine.Polygons;

/// <summary>Errors the polygon slices report.</summary>
internal static class PolygonErrors
{
    public static readonly Error HullFailed = new("Polygons.HullFailed", "No outline could be computed for the mesh's shadow.");

    public static readonly Error OffsetFailed = new("Polygons.OffsetFailed", "The offset left no outline behind.");

    public static readonly Error NonPositiveDistance = new("Polygons.NonPositiveDistance", "A buffered path needs a positive distance.");

    public static readonly Error EmptyPath = new("Polygons.EmptyPath", "The path has no points.");

    public static readonly Error NothingToUnion = new("Polygons.NothingToUnion", "None of the polygons has an area to union.");

    public static readonly Error TooFewPoints = new("Polygons.TooFewPoints", "A polygon needs at least three points.");

    public static readonly Error InvertedExtrusion = new("Polygons.InvertedExtrusion", "An extrusion's top must lie above its bottom.");

    public static Error ExtrusionFailed(string detail) => new("Polygons.ExtrusionFailed", detail);
}

/// <summary>
/// Conversions to and from Clipper's integer coordinates. Clipper works in 64-bit integers for
/// robustness, so coordinates are scaled up on the way in: at 1e5 a millimetre model keeps ten
/// nanometres of resolution with room to spare in the integer range.
/// </summary>
internal static class ClipperBridge
{
    private const double Scale = 100_000.0;

    public static Path64 ToPath(IEnumerable<Vec2> points) =>
        new(points.Select(point => new Point64((long)Math.Round(point.X * Scale), (long)Math.Round(point.Y * Scale))));

    public static ImmutableArray<Vec2> FromPath(Path64 path) =>
        [.. path.Select(point => new Vec2(point.X / Scale, point.Y / Scale))];

    public static double ToClipperDistance(double distance) => distance * Scale;

    /// <summary>The largest contour by area - the one footprint every consumer of these wants.</summary>
    public static Path64 Largest(Paths64 paths) => paths.OrderByDescending(path => Math.Abs(Clipper.Area(path))).First();
}

/// <summary>
/// Outline smoothing shared by the shadow and hull slices: resample towards 4-unit edges, then
/// relax, four times over. Tuned for millimetre models, where it takes the stair-stepping out of
/// a projected scan without moving the outline more than a fraction of a millimetre.
/// </summary>
internal static class OutlineSmoothing
{
    public static ImmutableArray<Vec2> Smooth(IEnumerable<Vec2> ring)
    {
        var curve = ring.Select(point => new Vec3(point.X, point.Y, 0)).ToList();

        for (var pass = 0; pass < 4; pass++)
        {
            var resampled = CurveResampling.SplitCollapse(curve, closed: true, maxEdge: 4.0, minEdge: 1.0);
            if (resampled is not null)
            {
                curve = resampled;
            }

            CurveResampling.Smooth(curve, closed: true, alpha: 0.1, iterations: 4);
        }

        return [.. curve.Select(point => new Vec2(point.X, point.Y))];
    }

    /// <summary>
    /// Drops each vertex onto the XY plane, merging the ones that land on each other.
    ///
    /// A closed mesh has a front and a back over the same footprint, so projecting it hands the
    /// same XY to many vertices - and to NetTopologySuite's incremental Delaunay those
    /// near-coincident sites are a degenerate subdivision it throws "Locate failed to converge"
    /// on. Snapping to a grid finer than any real feature and de-duplicating keeps it solvable.
    /// </summary>
    public static MultiPoint ProjectToPlane(IMesh mesh, GeometryFactory factory)
    {
        const double snap = 1e-4;

        var seen = new HashSet<(long, long)>(mesh.VertexCount);
        var points = new List<Point>(mesh.VertexCount);

        foreach (var vertex in mesh.Vertices)
        {
            var key = ((long)Math.Round(vertex.X / snap), (long)Math.Round(vertex.Y / snap));
            if (seen.Add(key))
            {
                points.Add(factory.CreatePoint(new Coordinate(key.Item1 * snap, key.Item2 * snap)));
            }
        }

        return factory.CreateMultiPoint([.. points]);
    }
}

/// <summary>Ask for the concave outline of a mesh's shadow.</summary>
public sealed record ProjectOutlineRequest(IMesh Mesh);

internal sealed class ProjectOutlineHandler
{
    public Result<PlanarPolygon> Handle(ProjectOutlineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var factory = new GeometryFactory();
        var hull = new ConcaveHull(OutlineSmoothing.ProjectToPlane(request.Mesh, factory))
        {
            Alpha = 0.4,
            MaximumEdgeLength = 4.0,
            MaximumEdgeLengthRatio = 0.2,
        };

        var geometry = hull.GetHull();
        if (geometry is null || geometry.IsEmpty)
        {
            return PolygonErrors.HullFailed;
        }

        var ring = geometry.Boundary.Coordinates.Select(c => new Vec2(c.X, c.Y)).Reverse();
        return PlanarPolygon.FromOuter(OutlineSmoothing.Smooth(ring));
    }
}

/// <summary>Ask for the convex hull of a mesh's shadow.</summary>
public sealed record ProjectConvexHullRequest(IMesh Mesh);

internal sealed class ProjectConvexHullHandler
{
    public Result<PlanarPolygon> Handle(ProjectConvexHullRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var factory = new GeometryFactory();
        var geometry = new ConvexHull(OutlineSmoothing.ProjectToPlane(request.Mesh, factory)).GetConvexHull();
        if (geometry is null || geometry.IsEmpty)
        {
            return PolygonErrors.HullFailed;
        }

        var ring = geometry.Coordinates.Select(c => new Vec2(c.X, c.Y)).ToList();
        if (ring.Count > 1 && ring[0] == ring[^1])
        {
            ring.RemoveAt(ring.Count - 1);
        }

        return PlanarPolygon.FromOuter(OutlineSmoothing.Smooth(ring));
    }
}

/// <summary>Ask for a polygon grown or inset.</summary>
public sealed record OffsetPolygonRequest(PlanarPolygon Polygon, double Distance);

internal sealed class OffsetPolygonHandler
{
    public Result<PlanarPolygon> Handle(OffsetPolygonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Polygon);

        if (request.Polygon.Outer.Length < 3)
        {
            return PolygonErrors.TooFewPoints;
        }

        var path = ClipperBridge.ToPath(request.Polygon.Outer);

        // Clipper offsets a closed path according to its winding, so a positive distance only
        // grows a counter-clockwise one. Outlines arrive either way round, so it is settled here -
        // otherwise an inset would grow half of them.
        if (Clipper.Area(path) < 0)
        {
            path.Reverse();
        }

        var offsetter = new ClipperOffset();
        offsetter.AddPath(path, JoinType.Round, EndType.Polygon);
        var solution = new Paths64();
        offsetter.Execute(ClipperBridge.ToClipperDistance(request.Distance), solution);

        return solution.Count == 0
            ? PolygonErrors.OffsetFailed
            : PlanarPolygon.FromOuter(ClipperBridge.FromPath(ClipperBridge.Largest(solution)));
    }
}

/// <summary>Ask for everything within a distance of an open path.</summary>
public sealed record BufferPathRequest(ImmutableArray<Vec2> Path, double Distance);

internal sealed class BufferPathHandler
{
    public Result<PlanarPolygon> Handle(BufferPathRequest request)
    {
        if (request.Path.IsDefaultOrEmpty)
        {
            return PolygonErrors.EmptyPath;
        }

        if (!(request.Distance > 0))
        {
            return PolygonErrors.NonPositiveDistance;
        }

        // Clipper needs a segment to sweep round ends along; a hair of length still rounds off
        // into the disc a single point should buffer to.
        var points = request.Path.Length == 1
            ? [request.Path[0], request.Path[0] + new Vec2(request.Distance * 1e-3, 0)]
            : request.Path;

        var offsetter = new ClipperOffset();
        offsetter.AddPath(ClipperBridge.ToPath(points), JoinType.Round, EndType.Round);
        var solution = new Paths64();
        offsetter.Execute(ClipperBridge.ToClipperDistance(request.Distance), solution);

        // A path that doubles back can enclose an island; only the outer contour matters.
        return solution.Count == 0
            ? PolygonErrors.OffsetFailed
            : PlanarPolygon.FromOuter(ClipperBridge.FromPath(ClipperBridge.Largest(solution)));
    }
}

/// <summary>Ask for overlapping polygons merged.</summary>
public sealed record UnionPolygonsRequest(ImmutableArray<PlanarPolygon> Polygons);

internal sealed class UnionPolygonsHandler
{
    public Result<PlanarPolygon> Handle(UnionPolygonsRequest request)
    {
        var subjects = new Paths64();
        foreach (var polygon in request.Polygons.IsDefault ? [] : request.Polygons)
        {
            if (polygon.Outer.Length < 3)
            {
                continue;
            }

            // The non-zero fill rule cancels overlaps wound against each other, so every
            // contour goes in the same way round.
            var path = ClipperBridge.ToPath(polygon.Outer);
            if (Clipper.Area(path) < 0)
            {
                path.Reverse();
            }

            subjects.Add(path);
        }

        if (subjects.Count == 0)
        {
            return PolygonErrors.NothingToUnion;
        }

        var solution = Clipper.Union(subjects, FillRule.NonZero);
        return solution.Count == 0
            ? PolygonErrors.NothingToUnion
            : PlanarPolygon.FromOuter(ClipperBridge.FromPath(ClipperBridge.Largest(solution)));
    }
}

/// <summary>Ask for a polygon extruded into a prism.</summary>
public sealed record ExtrudePolygonRequest(PlanarPolygon Polygon, double ZMin, double ZMax);

/// <summary>
/// The native kernel extrudes in one call, triangulating the caps and closing the walls, and its
/// result is manifold by construction. Where it is unavailable the prism is assembled from the
/// managed triangulator instead, which gives the same solid without that guarantee.
/// </summary>
internal sealed class ExtrudePolygonHandler
{
    public Result<IMesh> Handle(ExtrudePolygonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Polygon);

        if (request.Polygon.Outer.Length < 3)
        {
            return PolygonErrors.TooFewPoints;
        }

        if (!(request.ZMax > request.ZMin))
        {
            return PolygonErrors.InvertedExtrusion;
        }

        // Manifold reads outlines counter-clockwise and holes clockwise. The callers do not agree
        // on which way round they hand theirs over, so it is settled here rather than assumed.
        var contours = new List<IReadOnlyList<Vec2>> { Oriented(request.Polygon.Outer, clockwise: false) };
        contours.AddRange(request.Polygon.Holes.Where(hole => hole.Length >= 3).Select(hole => Oriented(hole, clockwise: true)));

        var metadata = new MeshMetadata("extrusion", "GeometryEngine.Polygons.Extrude");
        var native = ManifoldKernel.Extrude(contours, request.ZMin, request.ZMax, metadata);
        if (native.IsSuccess || native.Error.Code != ManifoldErrors.Unavailable.Code)
        {
            return native.IsSuccess ? native : PolygonErrors.ExtrusionFailed(native.Error.Description);
        }

        var (points, faces) = PolygonTriangulator.Triangulate(contours);
        if (faces.Count == 0)
        {
            return PolygonErrors.ExtrusionFailed("The polygon could not be triangulated.");
        }

        return PrismBuilder.Build(
            PrismBuilder.CounterClockwise(points, faces),
            [.. points.Select(p => new Vec3(p.X, p.Y, request.ZMin))],
            [.. points.Select(p => new Vec3(p.X, p.Y, request.ZMax))],
            metadata with { CreatedBy = "GeometryEngine.Polygons.Extrude (managed)" });
    }

    private static IReadOnlyList<Vec2> Oriented(ImmutableArray<Vec2> contour, bool clockwise)
    {
        var isClockwise = PlanarPolygon.SignedAreaOf(contour) < 0;
        return isClockwise == clockwise ? contour : [.. contour.Reverse()];
    }
}

/// <summary>Ask for outlines triangulated.</summary>
public sealed record TriangulateRequest(ImmutableArray<PlanarPolygon> Polygons);

internal sealed class TriangulateHandler
{
    public Result<PlanarTriangulation> Handle(TriangulateRequest request)
    {
        var contours = new List<IReadOnlyList<Vec2>>();
        foreach (var polygon in request.Polygons.IsDefault ? [] : request.Polygons)
        {
            if (polygon.Outer.Length < 3)
            {
                continue;
            }

            contours.Add(polygon.Outer);
            contours.AddRange(polygon.Holes.Where(hole => hole.Length >= 3).Select(hole => (IReadOnlyList<Vec2>)hole));
        }

        var (points, faces) = PolygonTriangulator.Triangulate(contours);
        if (faces.Count == 0)
        {
            return PolygonErrors.TooFewPoints;
        }

        return new PlanarTriangulation(
            [.. points],
            [.. PrismBuilder.CounterClockwise(points, faces).SelectMany(face => new[] { face.A, face.B, face.C })]);
    }
}

/// <summary>The <see cref="IPolygonOperations"/> facade over the polygon slices.</summary>
internal sealed class PolygonOperations : IPolygonOperations
{
    private readonly ProjectOutlineHandler _outline = new();
    private readonly ProjectConvexHullHandler _hull = new();
    private readonly OffsetPolygonHandler _offset = new();
    private readonly BufferPathHandler _buffer = new();
    private readonly UnionPolygonsHandler _union = new();
    private readonly ExtrudePolygonHandler _extrude = new();
    private readonly TriangulateHandler _triangulate = new();

    public Result<PlanarPolygon> ProjectOutline(IMesh mesh) => _outline.Handle(new ProjectOutlineRequest(mesh));

    public Result<PlanarPolygon> ProjectConvexHull(IMesh mesh) => _hull.Handle(new ProjectConvexHullRequest(mesh));

    public Result<PlanarPolygon> Offset(PlanarPolygon polygon, double distance) =>
        _offset.Handle(new OffsetPolygonRequest(polygon, distance));

    public Result<PlanarPolygon> BufferPath(ImmutableArray<Vec2> path, double distance) =>
        _buffer.Handle(new BufferPathRequest(path, distance));

    public Result<PlanarPolygon> Union(ImmutableArray<PlanarPolygon> polygons) =>
        _union.Handle(new UnionPolygonsRequest(polygons));

    public Result<IMesh> Extrude(PlanarPolygon polygon, double zMin, double zMax) =>
        _extrude.Handle(new ExtrudePolygonRequest(polygon, zMin, zMax));

    public Result<PlanarTriangulation> Triangulate(ImmutableArray<PlanarPolygon> polygons) =>
        _triangulate.Handle(new TriangulateRequest(polygons));

    /// <summary>
    /// Reversing each ring as well as reflecting it keeps an outline counter-clockwise and a hole
    /// clockwise; a reflection alone would swap them.
    /// </summary>
    public PlanarPolygon MirrorX(PlanarPolygon polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);

        static ImmutableArray<Vec2> Mirror(ImmutableArray<Vec2> ring) => [.. ring.Select(p => new Vec2(-p.X, p.Y)).Reverse()];

        return new PlanarPolygon(Mirror(polygon.Outer), [.. polygon.Holes.Select(Mirror)]);
    }
}
