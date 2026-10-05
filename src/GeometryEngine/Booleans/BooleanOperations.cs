using GeometryEngine.Internal;

namespace GeometryEngine.Booleans;

/// <summary>
/// The <see cref="IBooleans"/> facade. It owns nothing but the three handlers,
/// so the interface stays a thin seam over independent slices.
/// </summary>
internal sealed class BooleanOperations(IToleranceStrategy tolerance) : IBooleans
{
    private readonly UnionHandler _union = new(tolerance);
    private readonly SubtractHandler _subtract = new(tolerance);
    private readonly IntersectHandler _intersect = new(tolerance);

    public Result<IMesh> Union(IMesh meshA, IMesh meshB) => _union.Handle(new UnionRequest(meshA, meshB));

    public Result<IMesh> Subtract(IMesh meshA, IMesh meshB) => _subtract.Handle(new SubtractRequest(meshA, meshB));

    public Result<IMesh> Intersect(IMesh meshA, IMesh meshB) => _intersect.Handle(new IntersectRequest(meshA, meshB));

    public Result<IMesh> Union(ImmutableArray<IMesh> meshes)
    {
        meshes = meshes.IsDefault ? [] : meshes;
        var operands = BooleanOperands.Validate(meshes);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        if (meshes.Length == 1)
        {
            return Result.Success(meshes[0]);
        }

        var union = ComposedBooleans.Union(this, meshes);
        return union.IsFailure
            ? union
            : Result.Success(union.Value.WithMetadata(BooleanOperands.DescribeBatch(meshes, "Union")));
    }

    public Result<IMesh> Subtract(IMesh mesh, ImmutableArray<IMesh> tools)
    {
        tools = tools.IsDefault ? [] : tools;
        var operands = BooleanOperands.Validate([mesh, .. tools]);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        if (tools.IsEmpty)
        {
            return Result.Success(mesh);
        }

        var subtracted = ComposedBooleans.Subtract(this, mesh, tools);
        return subtracted.IsFailure
            ? subtracted
            : Result.Success(subtracted.Value.WithMetadata(BooleanOperands.DescribeBatch([mesh, .. tools], "Subtract")));
    }

    public Result<IMesh> Evaluate(Solid query) => Result.Failure<IMesh>(BooleanErrors.NotImplemented);

    public Result<MeshSplit> Split(IMesh mesh, Plane plane)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(plane);

        return mesh.IsEmpty
            ? Result.Failure<MeshSplit>(BooleanErrors.EmptyOperand)
            : ComposedBooleans.Split(this, mesh, plane);
    }
}
