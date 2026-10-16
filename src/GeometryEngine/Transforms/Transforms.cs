namespace GeometryEngine.Transforms;

/// <summary>Errors the transform slices report.</summary>
internal static class TransformErrors
{
    public static readonly Error MirroringScale = new(
        "Transforms.MirroringScale",
        "A scale with a negative or zero factor would turn the solid inside out. Scale by positive factors, or rebuild the mesh with reversed winding on purpose.");

    public static readonly Error NonFiniteTransform = new(
        "Transforms.NonFinite",
        "The transform parameters are not finite.");

    public static readonly Error InvalidAlignmentParameters = new(
        "Transforms.InvalidAlignmentParameters",
        "Alignment needs at least three sample points, a non-negative iteration limit, and a positive tolerance.");
}

/// <summary>
/// Applies a point-to-point map to every vertex and keeps the winding.
/// Shared by the three slices because the only thing that varies between them is
/// the map itself.
/// </summary>
internal static class VertexMap
{
    /// <param name="rigidTurn">
    /// For a map that keeps every distance - a translation or a rotation - how it turns a
    /// direction; null for one that does not. What has been measured of a rigidly moved input
    /// still describes the output, bounds and normals aside, and is carried across rather than
    /// left for the next caller to measure again. A scale is not rigid: it changes volume and
    /// area, and stretches slivers and gaps across the tolerance the audit reads.
    /// </param>
    public static Result<IMesh> Apply(IMesh mesh, Func<Vec3, Vec3> map, string operation, Func<Vec3, Vec3>? rigidTurn)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var moved = ImmutableArray.CreateBuilder<Vec3>(mesh.VertexCount);
        var min = map(mesh.Vertices[0]);
        var max = min;
        foreach (var vertex in mesh.Vertices)
        {
            var point = map(vertex);
            moved.Add(point);
            min = min.ComponentMin(point);
            max = max.ComponentMax(point);
        }

        var created = ImmutableMesh.Create(
            moved.MoveToImmutable(),
            mesh.Triangles,
            mesh.Metadata.CarriedThrough(MeshOperation.Transform, $"GeometryEngine.Transforms.{operation}"));

        if (rigidTurn is not null && created.IsSuccess && mesh is ImmutableMesh source && created.Value is ImmutableMesh result)
        {
            result.Measurements.CarryRigid(source.Measurements, rigidTurn, min, max);
        }

        return created;
    }
}

/// <summary>Ask for a mesh shifted by an offset.</summary>
public sealed record TranslateRequest(IMesh Mesh, Vec3 Offset);

internal sealed class TranslateHandler
{
    public Result<IMesh> Handle(TranslateRequest request) =>
        request.Offset.IsFinite
            ? VertexMap.Apply(request.Mesh, vertex => vertex + request.Offset, "Translate", MeshMeasurements.Unturned)
            : TransformErrors.NonFiniteTransform;
}

/// <summary>Ask for a mesh scaled about the origin.</summary>
public sealed record ScaleRequest(IMesh Mesh, Vec3 Factors);

internal sealed class ScaleHandler
{
    public Result<IMesh> Handle(ScaleRequest request)
    {
        var factors = request.Factors;

        if (!factors.IsFinite)
        {
            return TransformErrors.NonFiniteTransform;
        }

        if (factors.X <= 0 || factors.Y <= 0 || factors.Z <= 0)
        {
            return TransformErrors.MirroringScale;
        }

        return VertexMap.Apply(
            request.Mesh,
            vertex => new Vec3(vertex.X * factors.X, vertex.Y * factors.Y, vertex.Z * factors.Z),
            "Scale",
            rigidTurn: null);
    }
}

/// <summary>Ask for a mesh rotated about an axis through the origin.</summary>
public sealed record RotateRequest(IMesh Mesh, Direction Axis, double Radians);

/// <summary>
/// Rotation by Rodrigues' formula. Taking a <see cref="Direction"/> rather than a
/// raw vector means the handler never has to check for a zero-length axis: the
/// type system already did.
/// </summary>
internal sealed class RotateHandler
{
    public Result<IMesh> Handle(RotateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Axis);

        if (!double.IsFinite(request.Radians))
        {
            return TransformErrors.NonFiniteTransform;
        }

        var axis = request.Axis.Vector;
        var cosine = Math.Cos(request.Radians);
        var sine = Math.Sin(request.Radians);

        // About an axis through the origin, so the same map turns points and directions alike.
        Vec3 Rotate(Vec3 vertex) =>
            (vertex * cosine)
            + (axis.Cross(vertex) * sine)
            + (axis * (axis.Dot(vertex) * (1 - cosine)));

        return VertexMap.Apply(request.Mesh, Rotate, "Rotate", Rotate);
    }
}

/// <summary>Ask for a source mesh aligned rigidly onto a target mesh.</summary>
public sealed record AlignRequest(
    IMesh Source,
    IMesh Target,
    int MaxIterations = 50,
    double Tolerance = 1e-5,
    int MaxSamplePoints = 2000);

/// <summary>
/// Rigid Iterative Closest Point (ICP) registration, point-to-plane, against the target mesh's
/// shared spatial index.
/// </summary>
internal sealed class AlignHandler
{
    /// <summary>The share of closest-point pairs, nearest first, each step fits to.</summary>
    private const double InlierFraction = 0.9;

    public Result<AlignmentResult> Handle(AlignRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Target);

        if (request.Source.IsEmpty || request.Target.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (request.MaxIterations < 0 || request.MaxSamplePoints < 3 || !double.IsFinite(request.Tolerance) || request.Tolerance <= 0)
        {
            return TransformErrors.InvalidAlignmentParameters;
        }

        var source = request.Source;
        var target = request.Target;
        var targetIndex = Spatial.SharedIndexes.For(target).Inner;

        // Points spread evenly by area across the source surface, not its vertices: a coarse
        // mesh - a box has eight corners - would otherwise give ICP almost nothing to hold on to,
        // and a dense patch would outvote the rest of the shape.
        var sampleCount = request.MaxSamplePoints;
        var originalSamples = SampleSurface(source, sampleCount);

        // Start from centroid translation so disjoint meshes begin overlapping. Area-weighted, so
        // the start does not lean towards wherever either mesh happens to be finely tessellated.
        var totalRotation = Rotation.Identity;
        var totalTranslation = SurfaceCentroid(target) - SurfaceCentroid(source);

        var transformedSamples = new Vec3[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            transformedSamples[i] = originalSamples[i] + totalTranslation;
        }

        // Reused across iterations.
        var distances = new double[sampleCount];
        var inlierSources = new Vec3[sampleCount];
        var inlierTargets = new Vec3[sampleCount];
        var inlierNormals = new Vec3[sampleCount];

        var previousRmse = double.MaxValue;
        var iterationsRun = 0;

        for (var iter = 0; iter < request.MaxIterations; iter++)
        {
            iterationsRun = iter + 1;
            var closest = targetIndex.ClosestPoints(transformedSamples);

            // Trimmed ICP: the farthest tenth of the pairs are taken as outliers. A cut tied to the
            // median instead collapses as the fit closes - most samples land on the target, the
            // median goes to zero, and the few pairs still carrying the correction are thrown out.
            var squaredErrorSum = 0.0;
            for (var i = 0; i < sampleCount; i++)
            {
                distances[i] = closest[i].Distance;
                squaredErrorSum += distances[i] * distances[i];
            }

            var currentRmse = Math.Sqrt(squaredErrorSum / sampleCount);

            Array.Sort(distances);

            // Never fewer than three pairs, or a small sample count would trim itself to nothing.
            var kept = Math.Max(3, (int)Math.Ceiling(sampleCount * InlierFraction));
            var maxInlierDistance = distances[kept - 1];

            var pSum = Vec3.Zero;
            var inlierCount = 0;

            for (var i = 0; i < sampleCount; i++)
            {
                if (closest[i].Distance <= maxInlierDistance)
                {
                    inlierSources[inlierCount] = transformedSamples[i];
                    inlierTargets[inlierCount] = closest[i].Point;
                    inlierNormals[inlierCount] = closest[i].Normal;
                    pSum += transformedSamples[i];
                    inlierCount++;
                }
            }

            if (inlierCount < 3)
            {
                break;
            }

            var pMean = pSum / inlierCount;
            var (stepRotation, stepTranslation) = PointToPlaneStep(inlierSources, inlierTargets, inlierNormals, inlierCount, pMean);

            // Compose rigid transform: p' = R_step * (R_total * p + t_total) + t_step
            totalRotation = totalRotation.Then(stepRotation);
            totalTranslation = stepRotation.Apply(totalTranslation) + stepTranslation;

            for (var i = 0; i < sampleCount; i++)
            {
                transformedSamples[i] = totalRotation.Apply(originalSamples[i]) + totalTranslation;
            }

            if (Math.Abs(previousRmse - currentRmse) < request.Tolerance || currentRmse < request.Tolerance)
            {
                break;
            }

            previousRmse = currentRmse;
        }

        // Measure final RMSE on the aligned samples.
        var finalClosest = targetIndex.ClosestPoints(transformedSamples);
        var finalSquaredSum = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            finalSquaredSum += finalClosest[i].Distance * finalClosest[i].Distance;
        }

        var finalRmse = Math.Sqrt(finalSquaredSum / sampleCount);

        var alignedMeshResult = VertexMap.Apply(
            source,
            vertex => totalRotation.Apply(vertex) + totalTranslation,
            "Align",
            totalRotation.Apply);

        return alignedMeshResult.IsFailure
            ? Result.Failure<AlignmentResult>(alignedMeshResult.Error)
            : Result.Success(new AlignmentResult(
                alignedMeshResult.Value,
                totalRotation,
                totalTranslation,
                finalRmse,
                iterationsRun));
    }

    /// <summary>
    /// <paramref name="count"/> points spread across the surface in proportion to area: each lands
    /// in the triangle its share of the running area falls in, at a low-discrepancy position
    /// inside it, so the same mesh always yields the same points. A surface with no area at all
    /// falls back to triangle corners.
    /// </summary>
    private static Vec3[] SampleSurface(IMesh mesh, int count)
    {
        var triangleCount = mesh.TriangleCount;
        var cumulative = new double[triangleCount];
        var total = 0.0;
        for (var t = 0; t < triangleCount; t++)
        {
            var (a, b, c) = mesh.TriangleAt(t);
            total += (b - a).Cross(c - a).Length / 2;
            cumulative[t] = total;
        }

        var samples = new Vec3[count];
        if (!(total > 0) || !double.IsFinite(total))
        {
            for (var i = 0; i < count; i++)
            {
                samples[i] = mesh.TriangleAt(i % triangleCount).A;
            }

            return samples;
        }

        // The R2 sequence: the plastic number's powers give the most even 2D spread an
        // additive recurrence can.
        const double alpha1 = 0.7548776662466927;
        const double alpha2 = 0.5698402909980532;

        for (var i = 0; i < count; i++)
        {
            var share = (i + 0.5) / count * total;
            var t = Array.BinarySearch(cumulative, share);
            t = t >= 0 ? t : Math.Min(~t, triangleCount - 1);

            var u = (0.5 + (i * alpha1)) % 1;
            var v = (0.5 + (i * alpha2)) % 1;
            if (u + v > 1)
            {
                (u, v) = (1 - u, 1 - v);
            }

            var (a, b, c) = mesh.TriangleAt(t);
            samples[i] = a + ((b - a) * u) + ((c - a) * v);
        }

        return samples;
    }

    /// <summary>The area-weighted centre of the surface; the mean of the triangle corners where it has no area.</summary>
    private static Vec3 SurfaceCentroid(IMesh mesh)
    {
        var weighted = Vec3.Zero;
        var corners = Vec3.Zero;
        var total = 0.0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.TriangleAt(t);
            var area = (b - a).Cross(c - a).Length / 2;
            var centre = (a + b + c) / 3;
            weighted += centre * area;
            corners += centre;
            total += area;
        }

        return total > 0 && double.IsFinite(total)
            ? weighted / total
            : corners / mesh.TriangleCount;
    }

    /// <summary>
    /// One point-to-plane step: the small rigid motion minimising the summed squared distance of
    /// each source point to the tangent plane at its closest target point. Point-to-point fitting
    /// lets samples slide along flat faces and takes hundreds of steps to settle on a machined
    /// shape; measuring along the normal does not, and settles in a handful.
    /// </summary>
    /// <remarks>
    /// Linearised about <paramref name="centre"/> - (R p + t) is taken as p + w x p + t there - so
    /// the 6x6 normal equations stay well scaled wherever the meshes sit. A direction the surface
    /// does not pin (a sphere's spin, a plate's in-plane slide) is held still by a small ridge
    /// term rather than left to round-off.
    /// </remarks>
    private static (Rotation Rotation, Vec3 Translation) PointToPlaneStep(
        Vec3[] sources, Vec3[] targets, Vec3[] normals, int count, Vec3 centre)
    {
        var ata = new double[6, 6];
        var atb = new double[6];
        Span<double> row = stackalloc double[6];

        for (var i = 0; i < count; i++)
        {
            var n = normals[i];
            var c = (sources[i] - centre).Cross(n);
            row[0] = c.X;
            row[1] = c.Y;
            row[2] = c.Z;
            row[3] = n.X;
            row[4] = n.Y;
            row[5] = n.Z;

            var b = (targets[i] - sources[i]).Dot(n);
            for (var j = 0; j < 6; j++)
            {
                atb[j] += row[j] * b;
                for (var k = 0; k < 6; k++)
                {
                    ata[j, k] += row[j] * row[k];
                }
            }
        }

        var trace = 0.0;
        for (var j = 0; j < 6; j++)
        {
            trace += ata[j, j];
        }

        var ridge = (1e-9 * trace / 6) + double.Epsilon;
        for (var j = 0; j < 6; j++)
        {
            ata[j, j] += ridge;
        }

        var x = SolveSymmetric6(ata, atb);
        var spin = new Vec3(x[0], x[1], x[2]);
        var angle = spin.Length;
        var rotation = angle > 1e-15 && Direction.From(spin) is { HasValue: true } axis
            ? Rotation.FromAxisAngle(axis.Value, angle)
            : Rotation.Identity;

        // The fit turned the points about the centre; as a motion about the origin that is the
        // same turn followed by the centre's own displacement.
        var translation = centre + new Vec3(x[3], x[4], x[5]) - rotation.Apply(centre);
        return (rotation, translation);
    }

    /// <summary>Gaussian elimination with partial pivoting; a pivot that vanishes leaves its unknown at zero.</summary>
    private static double[] SolveSymmetric6(double[,] a, double[] b)
    {
        const int n = 6;
        var x = new double[n];
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (pivot != col)
            {
                for (var k = 0; k < n; k++)
                {
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                }

                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            if (Math.Abs(a[col, col]) < 1e-300)
            {
                continue;
            }

            for (var r = col + 1; r < n; r++)
            {
                var factor = a[r, col] / a[col, col];
                for (var k = col; k < n; k++)
                {
                    a[r, k] -= factor * a[col, k];
                }

                b[r] -= factor * b[col];
            }
        }

        for (var r = n - 1; r >= 0; r--)
        {
            if (Math.Abs(a[r, r]) < 1e-300)
            {
                continue;
            }

            var sum = b[r];
            for (var k = r + 1; k < n; k++)
            {
                sum -= a[r, k] * x[k];
            }

            x[r] = sum / a[r, r];
        }

        return x;
    }
}

/// <summary>The <see cref="IGeometryTransforms"/> facade over the transform slices.</summary>
internal sealed class GeometryTransforms : IGeometryTransforms
{
    private readonly TranslateHandler _translate = new();
    private readonly ScaleHandler _scale = new();
    private readonly RotateHandler _rotate = new();
    private readonly AlignHandler _align = new();

    public Result<IMesh> Translate(IMesh mesh, Vec3 offset) => _translate.Handle(new TranslateRequest(mesh, offset));

    public Result<IMesh> Scale(IMesh mesh, Vec3 factors) => _scale.Handle(new ScaleRequest(mesh, factors));

    public Result<IMesh> Rotate(IMesh mesh, Direction axis, double radians) =>
        _rotate.Handle(new RotateRequest(mesh, axis, radians));

    public Result<IMesh> Rotate(IMesh mesh, Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);

        // About the origin, so the same map turns points and directions alike.
        return VertexMap.Apply(mesh, rotation.Apply, "Rotate", rotation.Apply);
    }

    public Result<AlignmentResult> Align(
        IMesh source,
        IMesh target,
        int maxIterations = 50,
        double tolerance = 1e-5,
        int maxSamplePoints = 2000) =>
        _align.Handle(new AlignRequest(source, target, maxIterations, tolerance, maxSamplePoints));
}
