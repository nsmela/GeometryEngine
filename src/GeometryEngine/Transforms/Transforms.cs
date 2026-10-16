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
        "Alignment needs a positive sample count, a non-negative iteration limit, and a positive tolerance.");
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
/// Rigid Iterative Closest Point (ICP) registration using Horn's unit-quaternion absolute
/// orientation solver against the target mesh's shared spatial index.
/// </summary>
internal sealed class AlignHandler
{
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

        // Deterministic stride sampling across the source vertices.
        var sampleCount = Math.Min(source.VertexCount, request.MaxSamplePoints);
        var originalSamples = new Vec3[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var idx = (int)((long)i * source.VertexCount / sampleCount);
            originalSamples[i] = source.Vertices[idx];
        }

        // Start from centroid translation so disjoint meshes begin overlapping.
        var sourceCentroid = CentroidOf(originalSamples);
        var targetCentroid = CentroidOf(target.Vertices);

        var totalRotation = Rotation.Identity;
        var totalTranslation = targetCentroid - sourceCentroid;

        var transformedSamples = new Vec3[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            transformedSamples[i] = originalSamples[i] + totalTranslation;
        }

        var previousRmse = double.MaxValue;
        var iterationsRun = 0;

        for (var iter = 0; iter < request.MaxIterations; iter++)
        {
            iterationsRun = iter + 1;
            var closest = targetIndex.ClosestPoints(transformedSamples);

            // Compute median distance for robust outlier rejection (keep pairs within 3x median).
            var distances = new double[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                distances[i] = closest[i].Distance;
            }

            Array.Sort(distances);
            var median = distances[sampleCount / 2];
            var maxInlierDistance = median > 1e-9 ? median * 3.0 : double.MaxValue;

            var pSum = Vec3.Zero;
            var qSum = Vec3.Zero;
            var inlierCount = 0;
            var squaredErrorSum = 0.0;

            for (var i = 0; i < sampleCount; i++)
            {
                var d = closest[i].Distance;
                squaredErrorSum += d * d;

                if (d <= maxInlierDistance)
                {
                    pSum += transformedSamples[i];
                    qSum += closest[i].Point;
                    inlierCount++;
                }
            }

            var currentRmse = Math.Sqrt(squaredErrorSum / sampleCount);

            if (inlierCount < 3)
            {
                break;
            }

            var pMean = pSum / inlierCount;
            var qMean = qSum / inlierCount;

            // Cross-covariance matrix S = sum (p_i - pMean) * (q_i - qMean)^T
            double sxx = 0, sxy = 0, sxz = 0;
            double syx = 0, syy = 0, syz = 0;
            double szx = 0, szy = 0, szz = 0;

            for (var i = 0; i < sampleCount; i++)
            {
                if (closest[i].Distance > maxInlierDistance)
                {
                    continue;
                }

                var dp = transformedSamples[i] - pMean;
                var dq = closest[i].Point - qMean;

                sxx += dp.X * dq.X;
                sxy += dp.X * dq.Y;
                sxz += dp.X * dq.Z;

                syx += dp.Y * dq.X;
                syy += dp.Y * dq.Y;
                syz += dp.Y * dq.Z;

                szx += dp.Z * dq.X;
                szy += dp.Z * dq.Y;
                szz += dp.Z * dq.Z;
            }

            var stepRotation = OptimalRotationHorn(sxx, sxy, sxz, syx, syy, syz, szx, szy, szz);
            var stepTranslation = qMean - stepRotation.Apply(pMean);

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

    private static Vec3 CentroidOf(IReadOnlyList<Vec3> points)
    {
        var sum = Vec3.Zero;
        for (var i = 0; i < points.Count; i++)
        {
            sum += points[i];
        }

        return sum / points.Count;
    }

    /// <summary>
    /// Horn's closed-form quaternion solver: builds the symmetric 4x4 Davenport matrix N from the
    /// cross-covariance matrix S and extracts the eigenvector of its largest eigenvalue via Jacobi
    /// diagonalization.
    /// </summary>
    private static Rotation OptimalRotationHorn(
        double sxx, double sxy, double sxz,
        double syx, double syy, double syz,
        double szx, double szy, double szz)
    {
        double[,] a = new double[4, 4];
        a[0, 0] = sxx + syy + szz;
        a[0, 1] = syz - szy;
        a[0, 2] = szx - sxz;
        a[0, 3] = sxy - syx;

        a[1, 0] = a[0, 1];
        a[1, 1] = sxx - syy - szz;
        a[1, 2] = sxy + syx;
        a[1, 3] = szx + sxz;

        a[2, 0] = a[0, 2];
        a[2, 1] = a[1, 2];
        a[2, 2] = -sxx + syy - szz;
        a[2, 3] = syz + szy;

        a[3, 0] = a[0, 3];
        a[3, 1] = a[1, 3];
        a[3, 2] = a[2, 3];
        a[3, 3] = -sxx - syy + szz;

        double[,] v = new double[4, 4];
        for (var i = 0; i < 4; i++)
        {
            v[i, i] = 1.0;
        }

        // Exact Jacobi sweeps for a 4x4 symmetric matrix.
        for (var sweep = 0; sweep < 15; sweep++)
        {
            var offDiagonal = 0.0;
            for (var p = 0; p < 3; p++)
            {
                for (var q = p + 1; q < 4; q++)
                {
                    offDiagonal += Math.Abs(a[p, q]);
                }
            }

            if (offDiagonal < 1e-15)
            {
                break;
            }

            for (var p = 0; p < 3; p++)
            {
                for (var q = p + 1; q < 4; q++)
                {
                    var apq = a[p, q];
                    if (Math.Abs(apq) < 1e-15)
                    {
                        continue;
                    }

                    var tau = (a[q, q] - a[p, p]) / (2.0 * apq);
                    var t = tau >= 0
                        ? 1.0 / (tau + Math.Sqrt(1.0 + (tau * tau)))
                        : -1.0 / (-tau + Math.Sqrt(1.0 + (tau * tau)));

                    var c = 1.0 / Math.Sqrt(1.0 + (t * t));
                    var s = t * c;

                    a[p, p] -= t * apq;
                    a[q, q] += t * apq;
                    a[p, q] = 0.0;
                    a[q, p] = 0.0;

                    for (var r = 0; r < 4; r++)
                    {
                        if (r != p && r != q)
                        {
                            var arp = a[r, p];
                            var arq = a[r, q];
                            a[r, p] = a[p, r] = (c * arp) - (s * arq);
                            a[r, q] = a[q, r] = (s * arp) + (c * arq);
                        }

                        var vrp = v[r, p];
                        var vrq = v[r, q];
                        v[r, p] = (c * vrp) - (s * vrq);
                        v[r, q] = (s * vrp) + (c * vrq);
                    }
                }
            }
        }

        var maxCol = 0;
        for (var i = 1; i < 4; i++)
        {
            if (a[i, i] > a[maxCol, maxCol])
            {
                maxCol = i;
            }
        }

        var qw = v[0, maxCol];
        var qx = v[1, maxCol];
        var qy = v[2, maxCol];
        var qz = v[3, maxCol];
        if (qw < 0)
        {
            qw = -qw;
            qx = -qx;
            qy = -qy;
            qz = -qz;
        }

        var rotation = Rotation.FromQuaternion(qw, qx, qy, qz);
        return rotation.HasValue ? rotation.Value : Rotation.Identity;
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
