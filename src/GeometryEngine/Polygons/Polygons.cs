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

    public static readonly Error NonFiniteHeight = new("Polygons.NonFiniteHeight", "A slice needs a finite height.");

    public static readonly Error DegenerateHull = new("Polygons.DegenerateHull", "The points span no area, so they have no hull: fewer than three of them, or all in a line.");

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

    /// <summary>The outline and every hole, for a fill rule that tells them apart by nesting.</summary>
    public static Paths64 ToPaths(PlanarPolygon polygon)
    {
        var paths = new Paths64 { ToPath(polygon.Outer) };
        foreach (var hole in polygon.Holes.Where(hole => hole.Length >= 3))
        {
            paths.Add(ToPath(hole));
        }

        return paths;
    }

    /// <summary>
    /// Every region of a clipping result as a polygon of its own, holes attached. A tree rather
    /// than flat paths, because Clipper hands back outlines and holes interleaved and only the
    /// tree says which hole belongs to which outline. An island inside a hole is a region too.
    /// </summary>
    public static ImmutableArray<PlanarPolygon> FromTree(PolyTree64 tree)
    {
        var polygons = ImmutableArray.CreateBuilder<PlanarPolygon>();
        CollectOutlines(tree, polygons);
        return polygons.ToImmutable();
    }

    private static void CollectOutlines(PolyPath64 parent, ImmutableArray<PlanarPolygon>.Builder polygons)
    {
        for (var i = 0; i < parent.Count; i++)
        {
            var outline = parent[i];
            var holes = ImmutableArray.CreateBuilder<ImmutableArray<Vec2>>(outline.Count);
            for (var j = 0; j < outline.Count; j++)
            {
                holes.Add(FromPath(outline[j].Polygon!));
                CollectOutlines(outline[j], polygons);
            }

            polygons.Add(new PlanarPolygon(FromPath(outline.Polygon!), holes.ToImmutable()));
        }
    }
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

/// <summary>Ask for the convex hull of a set of points.</summary>
public sealed record ConvexHullRequest(ImmutableArray<Vec2> Points);

/// <summary>
/// The exact hull, unlike <see cref="ProjectConvexHullHandler"/>: those points are a scan's
/// shadow and want smoothing, these are placed deliberately and every one matters.
/// </summary>
internal sealed class ConvexHullHandler
{
    public Result<PlanarPolygon> Handle(ConvexHullRequest request)
    {
        var points = request.Points.IsDefault ? [] : request.Points.Where(point => point.IsFinite).ToList();
        if (points.Count < 3)
        {
            return PolygonErrors.DegenerateHull;
        }

        var factory = new GeometryFactory();
        var geometry = new ConvexHull([.. points.Select(p => new Coordinate(p.X, p.Y))], factory).GetConvexHull();

        // Points in a line hull to a segment, and one point to itself: neither encloses anything.
        if (geometry is not Polygon polygon)
        {
            return PolygonErrors.DegenerateHull;
        }

        var ring = polygon.Shell.Coordinates.Select(c => new Vec2(c.X, c.Y)).ToList();
        ring.RemoveAt(ring.Count - 1); // the shell repeats its first point to close

        if (PlanarPolygon.SignedAreaOf([.. ring]) < 0)
        {
            ring.Reverse();
        }

        return PlanarPolygon.FromOuter([.. ring]);
    }
}

/// <summary>Ask for the region two polygons share.</summary>
public sealed record IntersectPolygonsRequest(PlanarPolygon A, PlanarPolygon B);

/// <summary>Ask for the region of one polygon outside another.</summary>
public sealed record SubtractPolygonsRequest(PlanarPolygon A, PlanarPolygon B);

/// <summary>
/// Set operations between two polygons, holes included. Every region of the result comes back,
/// where offsets and unions keep only the largest: those build one footprint, but a set
/// operation that dropped an island would give the wrong answer rather than a tidier one.
/// </summary>
internal sealed class ClipPolygonsHandler
{
    public Result<ImmutableArray<PlanarPolygon>> Handle(IntersectPolygonsRequest request) =>
        Clip(request.A, request.B, ClipType.Intersection);

    public Result<ImmutableArray<PlanarPolygon>> Handle(SubtractPolygonsRequest request) =>
        Clip(request.A, request.B, ClipType.Difference);

    private static Result<ImmutableArray<PlanarPolygon>> Clip(PlanarPolygon a, PlanarPolygon b, ClipType operation)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Outer.Length < 3 || b.Outer.Length < 3)
        {
            return PolygonErrors.TooFewPoints;
        }

        // Even-odd tells an outline from its holes by nesting alone, so neither polygon has to
        // arrive wound a particular way round.
        var tree = new PolyTree64();
        Clipper.BooleanOp(operation, ClipperBridge.ToPaths(a), ClipperBridge.ToPaths(b), tree, FillRule.EvenOdd);

        return ClipperBridge.FromTree(tree);
    }
}

/// <summary>Ask for the outline a horizontal plane cuts through a mesh.</summary>
public sealed record SliceRequest(IMesh Mesh, double Height);

/// <summary>
/// Cuts every triangle that straddles the plane into a segment between the two edges it crosses,
/// then chains the segments into loops through the edges they share.
///
/// A crossing point is computed from its edge alone, endpoints taken in a fixed order, so the two
/// triangles either side of an edge produce the very same point and the chain closes exactly
/// rather than to within a tolerance. Edges are keyed by their endpoints' positions rather than
/// their indices, so a mesh that repeats a corner per triangle chains as well as a welded one.
/// A vertex lying exactly on the plane is counted as below it - any consistent choice gives
/// closed loops, and this one keeps a face resting on the plane out of the cut.
///
/// Only closed loops are kept. An open surface, or a non-manifold edge, leaves chains that do
/// not close, and an outline with a gap in it is not a polygon.
/// </summary>
internal sealed class SliceHandler
{
    public Result<ImmutableArray<PlanarPolygon>> Handle(SliceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (!double.IsFinite(request.Height))
        {
            return PolygonErrors.NonFiniteHeight;
        }

        var mesh = request.Mesh;
        var height = request.Height;
        var nodes = new Dictionary<(Vec3, Vec3), int>();
        var points = new List<Vec2>();
        var segmentsAt = new List<List<int>>();
        var segments = new List<(int A, int B)>();

        int Node(Vec3 p, Vec3 q)
        {
            var key = Compare(p, q) < 0 ? (p, q) : (q, p);
            if (!nodes.TryGetValue(key, out var node))
            {
                node = points.Count;
                nodes[key] = node;
                var t = (height - key.Item1.Z) / (key.Item2.Z - key.Item1.Z);
                var crossing = key.Item1.LerpTo(key.Item2, t);
                points.Add(new Vec2(crossing.X, crossing.Y));
                segmentsAt.Add([]);
            }

            return node;
        }

        // A triangle whose corners sit all on one side crosses nothing, and is most of any mesh:
        // it is passed over before anything is allocated or looked up for it.
        Span<int> crossings = stackalloc int[3];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.TriangleAt(t);
            var (aboveA, aboveB, aboveC) = (a.Z > height, b.Z > height, c.Z > height);
            if (aboveA == aboveB && aboveB == aboveC)
            {
                continue;
            }

            // Edges in the order a-b, b-c, c-a, as each crossing has always been found.
            var count = 0;
            if (aboveA != aboveB) { crossings[count++] = Node(a, b); }
            if (aboveB != aboveC) { crossings[count++] = Node(b, c); }
            if (aboveC != aboveA) { crossings[count++] = Node(c, a); }

            if (count != 2 || crossings[0] == crossings[1])
            {
                continue;
            }

            var segment = segments.Count;
            segments.Add((crossings[0], crossings[1]));
            segmentsAt[crossings[0]].Add(segment);
            segmentsAt[crossings[1]].Add(segment);
        }

        var loops = ImmutableArray.CreateBuilder<ImmutableArray<Vec2>>();
        var used = new bool[segments.Count];

        for (var start = 0; start < segments.Count; start++)
        {
            if (used[start])
            {
                continue;
            }

            var loop = ImmutableArray.CreateBuilder<Vec2>();
            var (first, node) = segments[start];
            var segment = start;
            var closed = false;

            while (true)
            {
                used[segment] = true;
                loop.Add(points[node]);

                if (node == first)
                {
                    closed = true;
                    break;
                }

                // Each crossing edge is shared by exactly two straddling triangles on a closed
                // surface. Anything else is a boundary or a non-manifold edge, and the chain is open.
                var here = segmentsAt[node];
                if (here.Count != 2)
                {
                    break;
                }

                segment = here[0] == segment ? here[1] : here[0];
                if (used[segment])
                {
                    break;
                }

                var (p, q) = segments[segment];
                node = p == node ? q : p;
            }

            if (closed && loop.Count >= 3)
            {
                loops.Add(loop.ToImmutable());
            }
        }

        return PolygonOperations.Nested(loops.ToImmutable());
    }

    private static int Compare(Vec3 p, Vec3 q) =>
        p.X != q.X ? p.X.CompareTo(q.X) : p.Y != q.Y ? p.Y.CompareTo(q.Y) : p.Z.CompareTo(q.Z);
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
    private readonly ConvexHullHandler _pointHull = new();
    private readonly ClipPolygonsHandler _clip = new();
    private readonly SliceHandler _slice = new();
    private readonly OffsetPolygonHandler _offset = new();
    private readonly BufferPathHandler _buffer = new();
    private readonly UnionPolygonsHandler _union = new();
    private readonly ExtrudePolygonHandler _extrude = new();
    private readonly TriangulateHandler _triangulate = new();

    public Result<PlanarPolygon> ProjectOutline(IMesh mesh) => _outline.Handle(new ProjectOutlineRequest(mesh));

    public Result<PlanarPolygon> ProjectConvexHull(IMesh mesh) => _hull.Handle(new ProjectConvexHullRequest(mesh));

    public Result<PlanarPolygon> ConvexHull(ImmutableArray<Vec2> points) => _pointHull.Handle(new ConvexHullRequest(points));

    public Result<ImmutableArray<PlanarPolygon>> Intersect(PlanarPolygon a, PlanarPolygon b) =>
        _clip.Handle(new IntersectPolygonsRequest(a, b));

    public Result<ImmutableArray<PlanarPolygon>> Subtract(PlanarPolygon a, PlanarPolygon b) =>
        _clip.Handle(new SubtractPolygonsRequest(a, b));

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

    /// <summary>
    /// The same nesting the triangulator reads its contours by, so a polygon built here
    /// triangulates and extrudes exactly as its loops would have. Outlines come back
    /// counter-clockwise and holes clockwise - not required by anything here, but it is what a
    /// consumer drawing them would otherwise have to settle itself.
    /// </summary>
    public ImmutableArray<PlanarPolygon> FromLoops(ImmutableArray<ImmutableArray<Vec2>> loops) => Nested(loops);

    public Result<ImmutableArray<PlanarPolygon>> Slice(IMesh mesh, double height) =>
        _slice.Handle(new SliceRequest(mesh, height));

    /// <summary><see cref="FromLoops"/>, for the slices that produce loops of their own.</summary>
    internal static ImmutableArray<PlanarPolygon> Nested(ImmutableArray<ImmutableArray<Vec2>> loops)
    {
        if (loops.IsDefaultOrEmpty)
        {
            return [];
        }

        var contours = loops.Where(loop => !loop.IsDefault).Select(loop => (IReadOnlyList<Vec2>)loop).ToList();

        return [.. PolygonTriangulator.Nest(contours).Select(group => new PlanarPolygon(
            Wound(group.Outer, counterClockwise: true),
            [.. group.Holes.Select(hole => Wound(hole, counterClockwise: false))]))];
    }

    private static ImmutableArray<Vec2> Wound(List<Vec2> ring, bool counterClockwise) =>
        PolygonTriangulator.SignedArea(ring) > 0 == counterClockwise ? [.. ring] : [.. Enumerable.Reverse(ring)];
}
