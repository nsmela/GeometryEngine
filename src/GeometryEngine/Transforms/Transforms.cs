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

/// <summary>The <see cref="IGeometryTransforms"/> facade over the transform slices.</summary>
internal sealed class GeometryTransforms : IGeometryTransforms
{
    private readonly TranslateHandler _translate = new();
    private readonly ScaleHandler _scale = new();
    private readonly RotateHandler _rotate = new();

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
}
