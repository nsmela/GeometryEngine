using GeometryEngine.Internal.Planar;
using GeometryEngine.Internal.Spatial;
using GeometryEngine.Polygons;

namespace GeometryEngine.Generators;

/// <summary>Errors the path generator slices report.</summary>
internal static class PathGeneratorErrors
{
    public static readonly Error PathTooShort = new("Generators.PathTooShort", "A path needs at least two points.");

    public static readonly Error RadiiMismatch = new("Generators.RadiiMismatch", "There must be exactly one radius for every path point.");

    public static readonly Error NonFinitePath = new("Generators.NonFinitePath", "The path has a point that is NaN or infinite.");

    public static readonly Error DegeneratePath = new("Generators.DegeneratePath", "Consecutive path points coincide, so the path has no direction there.");

    public static readonly Error TriangulationFailed = new("Generators.TriangulationFailed", "The buffered path could not be triangulated.");
}

/// <summary>Ask for a tube swept along a path.</summary>
public sealed record TubeRequest(TubeSpec Spec);

/// <summary>
/// A ring of vertices at every path point, joined ring to ring, with optional fan caps. The
/// ring's frame is carried from point to point by rotating it through the turn between one
/// direction and the next - parallel transport - so the tube does not twist around a bend the
/// way a frame rebuilt from a fixed up-vector at every point would.
/// </summary>
internal sealed class TubeHandler
{
    public Result<IMesh> Handle(TubeRequest request)
    {
        var spec = request.Spec;
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Path.IsDefault || spec.Path.Length < 2)
        {
            return PathGeneratorErrors.PathTooShort;
        }

        if (spec.Radii.IsDefault || spec.Radii.Length != spec.Path.Length)
        {
            return PathGeneratorErrors.RadiiMismatch;
        }

        if (spec.Radii.Any(radius => !(radius > 0)))
        {
            return GeneratorErrors.NonPositiveRadius;
        }

        if (spec.Segments < 3)
        {
            return GeneratorErrors.TooFewSegments;
        }

        if (spec.Path.Any(point => !point.IsFinite))
        {
            return PathGeneratorErrors.NonFinitePath;
        }

        // A painted stroke can repeat a point, which gives that point no direction. Dropping the
        // repeat loses nothing: the tube passes through the same place either way.
        var path = new List<Vec3> { spec.Path[0] };
        var radii = new List<double> { spec.Radii[0] };
        for (var i = 1; i < spec.Path.Length; i++)
        {
            if ((spec.Path[i] - path[^1]).LengthSquared > 1e-20)
            {
                path.Add(spec.Path[i]);
                radii.Add(spec.Radii[i]);
            }
        }

        if (path.Count < 2)
        {
            return PathGeneratorErrors.DegeneratePath;
        }

        var directions = new Vec3[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            directions[i] = Direction(path, i);
        }

        var builder = new MeshBuilder();
        var segments = spec.Segments;
        var (u, w) = OrthogonalBasis(directions[0]);

        for (var i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                u = Transport(u, directions[i - 1], directions[i]);
                u = MeshBvh.Normalise(u - (directions[i] * u.Dot(directions[i])));
                w = directions[i].Cross(u);
            }

            for (var segment = 0; segment < segments; segment++)
            {
                var angle = 2.0 * Math.PI * segment / segments;
                var radial = (u * Math.Cos(angle)) + (w * Math.Sin(angle));
                builder.AddVertex(path[i] + (radial * radii[i]));
            }
        }

        for (var i = 0; i < path.Count - 1; i++)
        {
            var ring = i * segments;
            var nextRing = (i + 1) * segments;

            for (var segment = 0; segment < segments; segment++)
            {
                var next = (segment + 1) % segments;
                builder.AddTriangle(ring + segment, ring + next, nextRing + segment);
                builder.AddTriangle(ring + next, nextRing + next, nextRing + segment);
            }
        }

        if (spec.Capped)
        {
            var startHub = builder.AddVertex(path[0]);
            for (var segment = 0; segment < segments; segment++)
            {
                builder.AddTriangle((segment + 1) % segments, segment, startHub);
            }

            var endHub = builder.AddVertex(path[^1]);
            var lastRing = (path.Count - 1) * segments;
            for (var segment = 0; segment < segments; segment++)
            {
                builder.AddTriangle(lastRing + segment, lastRing + ((segment + 1) % segments), endHub);
            }
        }

        return builder.ToMesh(new MeshMetadata("tube", "GeometryEngine.Generators.Tube"));
    }

    /// <summary>The tangent at a path point: the segment at an end, the bisector of the two segments between.</summary>
    private static Vec3 Direction(List<Vec3> path, int index)
    {
        if (index == 0)
        {
            return MeshBvh.Normalise(path[1] - path[0]);
        }

        if (index == path.Count - 1)
        {
            return MeshBvh.Normalise(path[index] - path[index - 1]);
        }

        var incoming = MeshBvh.Normalise(path[index] - path[index - 1]);
        var outgoing = MeshBvh.Normalise(path[index + 1] - path[index]);
        var bisector = MeshBvh.Normalise(incoming + outgoing);

        // A path that doubles straight back has no bisector; the incoming direction will do.
        return bisector == Vec3.Zero ? incoming : bisector;
    }

    private static (Vec3 U, Vec3 W) OrthogonalBasis(Vec3 direction)
    {
        var helper = Math.Abs(direction.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
        var u = MeshBvh.Normalise(direction.Cross(helper));
        return (u, direction.Cross(u));
    }

    /// <summary>Rotates <paramref name="vector"/> by the rotation taking <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static Vec3 Transport(Vec3 vector, Vec3 from, Vec3 to)
    {
        var axis = from.Cross(to);
        if (axis.LengthSquared <= 1e-16)
        {
            return vector;
        }

        var angle = Math.Acos(Math.Clamp(from.Dot(to), -1, 1));
        return Rotate(vector, MeshBvh.Normalise(axis), angle);
    }

    internal static Vec3 Rotate(Vec3 vector, Vec3 axis, double angle)
    {
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        return (vector * cosine) + (axis.Cross(vector) * sine) + (axis * (axis.Dot(vector) * (1 - cosine)));
    }
}

/// <summary>Ask for points along a bend.</summary>
public sealed record ArcRequest(double BendRadius, Vec3 Start, Vec3 StartDirection, Vec3 EndDirection, int Segments);

/// <summary>
/// The bend's centre sits <see cref="ArcRequest.BendRadius"/> from the start, perpendicular to
/// the starting direction within the plane of the two directions; the start point is then
/// swept about it until the tangent matches the end direction.
/// </summary>
internal sealed class ArcHandler
{
    public Result<ImmutableArray<Vec3>> Handle(ArcRequest request)
    {
        if (!request.Start.IsFinite || !request.StartDirection.IsFinite || !request.EndDirection.IsFinite ||
            !double.IsFinite(request.BendRadius))
        {
            return PathGeneratorErrors.NonFinitePath;
        }

        if (request.Segments < 1)
        {
            return GeneratorErrors.TooFewSegments;
        }

        var start = MeshBvh.Normalise(request.StartDirection);
        var end = MeshBvh.Normalise(request.EndDirection);
        if (start == Vec3.Zero || end == Vec3.Zero)
        {
            return PathGeneratorErrors.DegeneratePath;
        }

        if (start.Dot(end) >= 0.999)
        {
            return ImmutableArray.Create(request.Start);
        }

        var normal = start.Cross(end);
        if (normal.LengthSquared < 1e-6)
        {
            // Directly opposed: any plane containing the start direction will do.
            normal = start.Cross(Math.Abs(start.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY);
        }

        normal = MeshBvh.Normalise(normal);

        var centre = request.Start + (MeshBvh.Normalise(normal.Cross(start)) * request.BendRadius);
        var radial = request.Start - centre;
        var sweep = Math.Acos(Math.Clamp(start.Dot(end), -1, 1));

        var points = ImmutableArray.CreateBuilder<Vec3>(request.Segments + 1);
        for (var i = 0; i <= request.Segments; i++)
        {
            points.Add(centre + TubeHandler.Rotate(radial, normal, sweep * i / request.Segments));
        }

        return points.MoveToImmutable();
    }
}

/// <summary>Ask for an open path resampled and smoothed.</summary>
public sealed record ResamplePathRequest(ImmutableArray<Vec3> Path, double Spacing, int SmoothingIterations);

/// <summary>
/// Splits long segments and collapses short ones towards the spacing, then relaxes the interior
/// points. Painted strokes arrive with uneven spacing and hand jitter, and both show up in the
/// tube swept along them. The ends are restored exactly afterwards: a stroke must start and end
/// where it was painted.
/// </summary>
internal sealed class ResamplePathHandler
{
    public Result<ImmutableArray<Vec3>> Handle(ResamplePathRequest request)
    {
        if (request.Path.IsDefaultOrEmpty)
        {
            return PathGeneratorErrors.PathTooShort;
        }

        if (request.Path.Length < 3 || !(request.Spacing > 0))
        {
            return request.Path;
        }

        var curve = request.Path.ToList();
        var resampled = CurveResampling.SplitCollapse(curve, closed: false, request.Spacing, request.Spacing / 4.0);
        if (resampled is not null && resampled.Count >= 2)
        {
            curve = resampled;
        }

        if (request.SmoothingIterations > 0 && curve.Count > 2)
        {
            CurveResampling.Smooth(curve, closed: false, alpha: 0.15, request.SmoothingIterations);
        }

        curve[0] = request.Path[0];
        curve[^1] = request.Path[^1];

        return ImmutableArray.CreateRange(curve);
    }
}

/// <summary>Ask for a path extruded across a surface.</summary>
public sealed record DrapedPathRequest(DrapedPathSpec Spec);

/// <summary>
/// Buffers the path's shadow into an outline, triangulates it, and raises every point into a
/// column: the bottom on the surface below it (or at the path's own height), sunk by the
/// depth, and the top at the flat height. Each point takes its own height, which is why this
/// cannot be a plain extrusion.
/// </summary>
internal sealed class DrapedPathHandler(BufferPathHandler buffer)
{
    /// <summary>How far above a point the downward ray starts, so it clears anything the surface could hold.</summary>
    private const double RayLift = 1000.0;

    private readonly BufferPathHandler _buffer = buffer;

    public Result<IMesh> Handle(DrapedPathRequest request)
    {
        var spec = request.Spec;
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Path.IsDefault || spec.Path.Length < 2)
        {
            return PathGeneratorErrors.PathTooShort;
        }

        if (!(spec.Radius > 0))
        {
            return GeneratorErrors.NonPositiveRadius;
        }

        var outline = _buffer.Handle(new BufferPathRequest([.. spec.Path.Select(p => new Vec2(p.X, p.Y))], spec.Radius));
        if (outline.IsFailure)
        {
            return Result.Failure<IMesh>(outline.Error);
        }

        var (points, faces) = PolygonTriangulator.Triangulate([outline.Value.Outer]);
        if (faces.Count == 0)
        {
            return PathGeneratorErrors.TriangulationFailed;
        }

        var surface = spec.Surface.HasValue && !spec.Surface.Value.IsEmpty ? new MeshBvh(spec.Surface.Value) : null;

        var bottoms = new Vec3[points.Count];
        var tops = new Vec3[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var z = HeightAlongPath(spec.Path, points[i]);
            if (surface is not null)
            {
                var origin = new Vec3(points[i].X, points[i].Y, z + RayLift);
                if (surface.Raycast(origin, -Vec3.UnitZ, out var distance, out _))
                {
                    z = origin.Z - distance;
                }
            }

            bottoms[i] = new Vec3(points[i].X, points[i].Y, z - spec.Depth);

            // A flat top, but never below the surface it rises from.
            tops[i] = new Vec3(points[i].X, points[i].Y, Math.Max(z + 1.0, spec.TopHeight));
        }

        return PrismBuilder.Build(
            PrismBuilder.CounterClockwise(points, faces),
            bottoms,
            tops,
            new MeshMetadata("draped path", "GeometryEngine.Generators.DrapedPath"));
    }

    /// <summary>The height of the path where it passes closest to a point, in plan.</summary>
    private static double HeightAlongPath(ImmutableArray<Vec3> path, Vec2 point)
    {
        var height = path[0].Z;
        var best = double.MaxValue;

        for (var i = 0; i < path.Length - 1; i++)
        {
            var a = new Vec2(path[i].X, path[i].Y);
            var b = new Vec2(path[i + 1].X, path[i + 1].Y);
            var segment = b - a;
            if (segment.LengthSquared < 1e-8)
            {
                continue;
            }

            var t = Math.Clamp((point - a).Dot(segment) / segment.LengthSquared, 0, 1);
            var squared = (point - a.LerpTo(b, t)).LengthSquared;
            if (squared < best)
            {
                best = squared;
                height = path[i].Z + (t * (path[i + 1].Z - path[i].Z));
            }
        }

        return height;
    }
}
