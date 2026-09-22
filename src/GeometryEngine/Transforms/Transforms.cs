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
    public static Result<IMesh> Apply(IMesh mesh, Func<Vec3, Vec3> map, string operation)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var moved = ImmutableArray.CreateBuilder<Vec3>(mesh.VertexCount);
        foreach (var vertex in mesh.Vertices)
        {
            moved.Add(map(vertex));
        }

        return ImmutableMesh.Create(
            moved.MoveToImmutable(),
            mesh.Triangles,
            mesh.Metadata.CarriedThrough(MeshOperation.Transform, $"GeometryEngine.Transforms.{operation}"));
    }
}

/// <summary>Ask for a mesh shifted by an offset.</summary>
public sealed record TranslateRequest(IMesh Mesh, Vec3 Offset);

internal sealed class TranslateHandler
{
    public Result<IMesh> Handle(TranslateRequest request) =>
        request.Offset.IsFinite
            ? VertexMap.Apply(request.Mesh, vertex => vertex + request.Offset, "Translate")
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
            "Scale");
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

        return VertexMap.Apply(
            request.Mesh,
            vertex => (vertex * cosine)
                      + (axis.Cross(vertex) * sine)
                      + (axis * (axis.Dot(vertex) * (1 - cosine))),
            "Rotate");
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
}
