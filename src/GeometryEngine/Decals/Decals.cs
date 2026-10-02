using GeometryEngine.Internal.Planar;
using GeometryEngine.Internal.Spatial;
using GeometryEngine.Spatial;

namespace GeometryEngine.Decals;

/// <summary>Errors the decal slices report.</summary>
internal static class DecalErrors
{
    public static readonly Error EmptyOutlines = new("Decals.EmptyOutlines", "There are no outlines with at least three points to build from.");

    public static readonly Error TriangulationFailed = new("Decals.TriangulationFailed", "The outlines could not be triangulated.");
}

/// <summary>Ask for outlines built into a solid in a surface frame.</summary>
public sealed record BuildPrismRequest(DecalPrismSpec Spec);

/// <summary>
/// Triangulates the outlines and raises each point into a column along the local normal. Without
/// a surface the columns stand on the frame's plane. With one, the frame is first walked along
/// the surface in both directions to make a baseline that follows its curvature, and each point
/// is placed off that baseline and settled onto the nearest part of the surface - so a label
/// wraps around a curve rather than cutting through it on a chord.
///
/// Distances are in the model's units and tuned for millimetres: the baseline is sampled every
/// half unit, two units past each end of the text.
/// </summary>
internal sealed class BuildPrismHandler
{
    /// <summary>How close to the baseline a point sits to be placed on it directly rather than settled.</summary>
    private const double BaselineTolerance = 1e-4;

    /// <summary>Distance between baseline samples. Smaller follows curvature more closely at the cost of more queries.</summary>
    private const double BaselineStep = 0.5;

    /// <summary>Baseline marched past each end of the text, so glyphs at the extremes still sample a frame either side.</summary>
    private const double BaselineMargin = 2.0;

    public Result<IMesh> Handle(BuildPrismRequest request)
    {
        var spec = request.Spec;
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Frame);

        if (spec.Outlines.IsDefaultOrEmpty)
        {
            return DecalErrors.EmptyOutlines;
        }

        var outlines = spec.MaxEdgeLength > 0
            ? spec.Outlines.Select(polygon => Subdivide(polygon, spec.MaxEdgeLength))
            : spec.Outlines;

        var contours = new List<IReadOnlyList<Vec2>>();
        foreach (var polygon in outlines)
        {
            if (polygon.Outer.Length < 3)
            {
                continue;
            }

            contours.Add(polygon.Outer);
            contours.AddRange(polygon.Holes.Where(hole => hole.Length >= 3).Select(hole => (IReadOnlyList<Vec2>)hole));
        }

        if (contours.Count == 0)
        {
            return DecalErrors.EmptyOutlines;
        }

        var (points, rawFaces) = PolygonTriangulator.Triangulate(contours);
        if (rawFaces.Count == 0)
        {
            return DecalErrors.TriangulationFailed;
        }

        var frame = spec.Frame;
        var surfacePoints = new Vec3[points.Count];
        var normals = new Vec3[points.Count];

        // An index the caller already holds is theirs: query it and leave it open. Otherwise the
        // surface's own index, kept with it, so a prism laid again and again on one surface -
        // dragged across it, or one per label - builds it once.
        var supplied = spec.SurfaceIndex.HasValue ? SharedIndexes.Unwrap(spec.SurfaceIndex.Value) : null;
        if (supplied is not null)
        {
            PlaceOnSurface(supplied, frame, points, surfacePoints, normals);
        }
        else if (spec.Surface.HasValue && !spec.Surface.Value.IsEmpty)
        {
            PlaceOnSurface(SharedIndexes.For(spec.Surface.Value).Inner, frame, points, surfacePoints, normals);
        }
        else
        {
            for (var i = 0; i < points.Count; i++)
            {
                surfacePoints[i] = frame.ToWorld(points[i].X, points[i].Y, 0);
                normals[i] = frame.N;
            }
        }

        var bottoms = new Vec3[points.Count];
        var tops = new Vec3[points.Count];
        var top = spec.Depth + spec.Overshoot;
        for (var i = 0; i < points.Count; i++)
        {
            bottoms[i] = surfacePoints[i] + (normals[i] * spec.Sink);
            tops[i] = surfacePoints[i] + (normals[i] * top);
        }

        return PrismBuilder.Build(
            PrismBuilder.CounterClockwise(points, rawFaces),
            bottoms,
            tops,
            new MeshMetadata("decal prism", "GeometryEngine.Decals.BuildPrism"));
    }

    private static void PlaceOnSurface(SpatialIndex index, SurfaceFrame frame, List<Vec2> points, Vec3[] surfacePoints, Vec3[] normals)
    {
        // Seeded from the first point, not from 0: seeding at 0 forces the range to straddle the
        // origin, which silently widens the baseline for outlines not already centred on it.
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var baseline = Baseline(index.Tree, frame, minX, maxX);

        var proposed = new Vec3[points.Count];
        var frames = new BaselineFrame[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            frames[i] = Sample(baseline, points[i].X);
            proposed[i] = frames[i].Position + (frames[i].V * points[i].Y);
        }

        // Every off-baseline point asks the same question of the surface independently: one batch.
        var nearest = index.ClosestPoints(proposed);

        for (var i = 0; i < points.Count; i++)
        {
            if (Math.Abs(points[i].Y) < BaselineTolerance)
            {
                surfacePoints[i] = frames[i].Position;
                normals[i] = frames[i].N;
                continue;
            }

            if (nearest[i].Triangle < 0)
            {
                surfacePoints[i] = proposed[i];
                normals[i] = frames[i].N;
                continue;
            }

            var normal = nearest[i].Normal == Vec3.Zero ? frames[i].N : nearest[i].Normal;
            if (normal.Dot(frames[i].N) < 0)
            {
                normal = -normal;
            }

            // Slide onto the plane of the nearest triangle along its normal.
            var (corner, _, _) = index.Tree.Triangle(nearest[i].Triangle);
            surfacePoints[i] = proposed[i] - (normal * (proposed[i] - corner).Dot(normal));
            normals[i] = normal;
        }
    }

    private static PlanarPolygon Subdivide(PlanarPolygon polygon, double maxEdge) =>
        new(SubdivideRing(polygon.Outer, maxEdge), [.. polygon.Holes.Select(hole => SubdivideRing(hole, maxEdge))]);

    private static ImmutableArray<Vec2> SubdivideRing(ImmutableArray<Vec2> ring, double maxEdge)
    {
        if (ring.Length < 3)
        {
            return ring;
        }

        var points = ImmutableArray.CreateBuilder<Vec2>();
        for (var i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            points.Add(a);

            var length = a.DistanceTo(b);
            if (length <= maxEdge)
            {
                continue;
            }

            var pieces = (int)Math.Ceiling(length / maxEdge);
            for (var s = 1; s < pieces; s++)
            {
                points.Add(a.LerpTo(b, (double)s / pieces));
            }
        }

        return points.ToImmutable();
    }

    private readonly record struct BaselineFrame(double ArcLength, Vec3 Position, Vec3 U, Vec3 V, Vec3 N);

    /// <summary>
    /// Walks the surface out from the frame's origin in both directions, re-seating each step
    /// onto the mesh and carrying the tangent frame with it.
    /// </summary>
    private static List<BaselineFrame> Baseline(MeshBvh surface, SurfaceFrame frame, double minX, double maxX)
    {
        var forward = March(surface, frame, Math.Max(0, maxX + BaselineMargin), forward: true);
        var backward = March(surface, frame, Math.Abs(Math.Min(0, minX - BaselineMargin)), forward: false);

        var frames = new List<BaselineFrame>(forward.Count + backward.Count + 1);
        for (var i = backward.Count - 1; i >= 0; i--)
        {
            frames.Add(backward[i]);
        }

        frames.Add(new BaselineFrame(0, frame.Origin, frame.U, frame.V, frame.N));
        frames.AddRange(forward);
        return frames;
    }

    private static List<BaselineFrame> March(MeshBvh surface, SurfaceFrame frame, double distance, bool forward)
    {
        var frames = new List<BaselineFrame>();
        var steps = (int)Math.Ceiling(distance / BaselineStep);

        var position = frame.Origin;
        var u = forward ? frame.U : -frame.U;
        var v = frame.V;
        var n = frame.N;

        for (var i = 1; i <= steps; i++)
        {
            var arcLength = forward ? i * BaselineStep : -i * BaselineStep;
            var proposed = position + (u * BaselineStep);

            if (surface.ClosestPoint(proposed, out _, out var triangle, out _))
            {
                var normal = surface.TriangleNormal(triangle);
                if (normal == Vec3.Zero)
                {
                    normal = n;
                }

                if (normal.Dot(n) < 0)
                {
                    normal = -normal;
                }

                // Slide onto the triangle's plane, then re-orthogonalise the frame against the new
                // normal so the next step travels along the surface.
                var (corner, _, _) = surface.Triangle(triangle);
                position = proposed - (normal * (proposed - corner).Dot(normal));

                var projected = u - (normal * u.Dot(normal));
                if (projected.LengthSquared > 1e-8)
                {
                    u = MeshBvh.Normalise(projected);
                }

                var nextV = normal.Cross(forward ? u : -u);
                if (nextV.LengthSquared > 1e-8)
                {
                    nextV = MeshBvh.Normalise(nextV);
                    v = nextV.Dot(v) < 0 ? -nextV : nextV;
                }

                n = normal;
            }
            else
            {
                position = proposed;
            }

            frames.Add(new BaselineFrame(arcLength, position, forward ? u : -u, v, n));
        }

        return frames;
    }

    private static BaselineFrame Sample(List<BaselineFrame> frames, double u)
    {
        if (frames.Count == 1 || u <= frames[0].ArcLength)
        {
            return frames[0];
        }

        if (u >= frames[^1].ArcLength)
        {
            return frames[^1];
        }

        var low = 0;
        var high = frames.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (frames[mid].ArcLength < u)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        var first = frames[Math.Max(0, low - 1)];
        var second = frames[Math.Min(frames.Count - 1, Math.Max(0, low - 1) + 1)];

        var span = second.ArcLength - first.ArcLength;
        var t = span > 1e-6 ? Math.Clamp((u - first.ArcLength) / span, 0, 1) : 0;

        return new BaselineFrame(
            u,
            first.Position.LerpTo(second.Position, t),
            MeshBvh.Normalise(first.U.LerpTo(second.U, t)),
            MeshBvh.Normalise(first.V.LerpTo(second.V, t)),
            MeshBvh.Normalise(first.N.LerpTo(second.N, t)));
    }
}

/// <summary>Ask for a prism laid onto a surface.</summary>
public sealed record ProjectPrismRequest(IMesh Surface, SurfaceFrame Frame, IMesh Prism);

/// <summary>
/// Each vertex is anchored on the frame's plane, fired at the surface from well in front of it
/// along the normal, and re-raised off the hit by the height it had above the frame. A ray that
/// misses is fired from behind instead; one that misses both ways leaves the vertex on the plane
/// and reports that the prism overhangs the surface.
/// </summary>
internal sealed class ProjectPrismHandler
{
    /// <summary>How far in front of the surface the ray starts. Must clear the tallest model this is used on.</summary>
    private const double RayStandOff = 150.0;

    /// <summary>A surface turned more than 60 degrees from the frame is too curved for the label to sit on.</summary>
    private const double MaxDeviationDot = 0.5;

    /// <param name="index">
    /// The caller's own index over the same surface, when they hold one. Building a tree costs
    /// far more than casting the rays does, so a caller projecting repeatedly onto an unchanging
    /// surface passes theirs rather than paying for a new one every call.
    /// </param>
    public Result<ProjectedDecal> Handle(ProjectPrismRequest request, SpatialIndex? index = null)
    {
        ArgumentNullException.ThrowIfNull(request.Surface);
        ArgumentNullException.ThrowIfNull(request.Frame);
        ArgumentNullException.ThrowIfNull(request.Prism);

        if (request.Surface.IsEmpty || request.Prism.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var frame = request.Frame;
        var surface = (index ?? SharedIndexes.For(request.Surface).Inner).Tree;
        var towards = -frame.N;
        var vertices = request.Prism.Vertices;

        var projected = new Vec3[vertices.Length];
        var missed = 0;
        var tooCurved = 0;

        Parallel.For(0, vertices.Length, i =>
        {
            var local = frame.ToLocal(vertices[i]);
            var anchor = frame.ToWorld(local.X, local.Y, 0);
            var hitNormal = frame.N;
            Vec3 hit;

            var origin = anchor + (frame.N * RayStandOff);
            if (surface.Raycast(origin, towards, out var distance, out var triangle))
            {
                hit = origin + (towards * distance);

                var normal = surface.TriangleNormal(triangle);
                if (normal != Vec3.Zero)
                {
                    hitNormal = normal;
                }

                if (hitNormal.Dot(frame.N) < MaxDeviationDot)
                {
                    Interlocked.Increment(ref tooCurved);
                }
            }
            else
            {
                // The anchor may sit past the far side of the surface: fire the other way.
                var reverse = anchor - (frame.N * RayStandOff);
                if (surface.Raycast(reverse, frame.N, out var reverseDistance, out _))
                {
                    hit = reverse + (frame.N * reverseDistance);
                }
                else
                {
                    Interlocked.Increment(ref missed);
                    hit = anchor;
                }
            }

            projected[i] = hit + (hitNormal * local.Z);
        });

        var mesh = ImmutableMesh.Create(
            [.. projected],
            request.Prism.Triangles,
            request.Prism.Metadata.CarriedThrough(MeshOperation.Transform));
        return mesh.IsFailure
            ? Result.Failure<ProjectedDecal>(mesh.Error)
            : new ProjectedDecal(mesh.Value, missed > 0, tooCurved > 0);
    }
}

/// <summary>The <see cref="IDecalOperations"/> facade over the decal slices.</summary>
internal sealed class DecalOperations : IDecalOperations
{
    private readonly BuildPrismHandler _build = new();
    private readonly ProjectPrismHandler _project = new();

    public Result<IMesh> BuildPrism(DecalPrismSpec spec) => _build.Handle(new BuildPrismRequest(spec));

    public Result<ProjectedDecal> ProjectPrism(IMesh surface, SurfaceFrame frame, IMesh prism) =>
        _project.Handle(new ProjectPrismRequest(surface, frame, prism));

    public Result<ProjectedDecal> ProjectPrism(ISpatialIndex surface, SurfaceFrame frame, IMesh prism)
    {
        ArgumentNullException.ThrowIfNull(surface);

        return _project.Handle(new ProjectPrismRequest(surface.Mesh, frame, prism), SharedIndexes.Unwrap(surface));
    }
}
