using BasicResults;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Booleans;

/// <summary>
/// An <see cref="IBooleans"/> implementation backed by the native Manifold library.
/// Produces guaranteed watertight, 2-manifold output with high performance.
///
/// Where a fallback is supplied, an operation the native kernel declines is retried on it.
/// That is a genuine change of guarantee, not a transparent retry: the managed BSP kernel
/// does not promise watertight output, and on the meshes Manifold rejects it frequently
/// does not deliver it. So every result records which kernel produced it, and whether the
/// operands were welded on the way in, in <see cref="MeshMetadata.CreatedBy"/> - a caller
/// sending geometry to a printer can tell a guaranteed result from a best-effort one.
/// </summary>
internal sealed class ManifoldBooleanOperations(IBooleans? fallback = null) : IBooleans
{
    /// <summary>Recorded on results the native kernel produced from valid 2-manifold operands.</summary>
    public const string NativeProducer = "GeometryEngine.Booleans.Manifold";

    /// <summary>Recorded when an operand had to be welded before the native kernel would accept it.</summary>
    public const string NativeMergedProducer = "GeometryEngine.Booleans.Manifold (operands merged)";

    /// <summary>Recorded when the native kernel declined and the managed BSP kernel was used instead.</summary>
    public const string FallbackProducer = "GeometryEngine.Booleans.Bsp (Manifold declined)";

    private readonly IBooleans? _fallback = fallback;

    public Result<IMesh> Union(IMesh meshA, IMesh meshB) =>
        Run(meshA, meshB, "Union", ManifoldKernel.Union, static (f, a, b) => f.Union(a, b));

    public Result<IMesh> Subtract(IMesh meshA, IMesh meshB) =>
        Run(meshA, meshB, "Subtract", ManifoldKernel.Subtract, static (f, a, b) => f.Subtract(a, b));

    public Result<IMesh> Intersect(IMesh meshA, IMesh meshB) =>
        Run(meshA, meshB, "Intersect", ManifoldKernel.Intersect, static (f, a, b) => f.Intersect(a, b));

    public Result<IMesh> Union(ImmutableArray<IMesh> meshes)
    {
        meshes = meshes.IsDefault ? [] : meshes;
        var operands = BooleanOperands.Validate(meshes);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        return meshes.Length == 1
            ? Result.Success(meshes[0])
            : RunBatch(meshes, "Union", ManifoldOpType.Add, f => f.Union(meshes));
    }

    public Result<IMesh> Subtract(IMesh mesh, ImmutableArray<IMesh> tools)
    {
        tools = tools.IsDefault ? [] : tools;
        ImmutableArray<IMesh> operands = [mesh, .. tools];
        var valid = BooleanOperands.Validate(operands);
        if (valid.IsFailure)
        {
            return Result.Failure<IMesh>(valid.Error);
        }

        return tools.IsEmpty
            ? Result.Success(mesh)
            : RunBatch(operands, "Subtract", ManifoldOpType.Subtract, f => f.Subtract(mesh, tools));
    }

    public Result<MeshSplit> Split(IMesh mesh, Plane plane)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(plane);

        if (mesh.IsEmpty)
        {
            return Result.Failure<MeshSplit>(BooleanErrors.EmptyOperand);
        }

        var result = ManifoldKernel.Split(
            mesh, plane, BooleanOperands.DescribeHalf(mesh, "front"), BooleanOperands.DescribeHalf(mesh, "back"));

        if (result.IsSuccess)
        {
            return new MeshSplit(Produced(result.Value.Front), Produced(result.Value.Back));
        }

        if (_fallback is null || !ShouldFallBack(result.Error))
        {
            return Result.Failure<MeshSplit>(result.Error);
        }

        var fallen = _fallback.Split(mesh, plane);
        return fallen.IsFailure
            ? fallen
            : new MeshSplit(FellBack(fallen.Value.Front), FellBack(fallen.Value.Back));
    }

    private Result<IMesh> RunBatch(
        ImmutableArray<IMesh> operands,
        string operation,
        ManifoldOpType op,
        Func<IBooleans, Result<IMesh>> fallback)
    {
        var result = ManifoldKernel.Batch(operands, op, BooleanOperands.DescribeBatch(operands, operation));
        if (result.IsSuccess)
        {
            return Result.Success(Produced(result.Value));
        }

        if (_fallback is null || !ShouldFallBack(result.Error))
        {
            return Result.Failure<IMesh>(result.Error);
        }

        var fallen = fallback(_fallback);
        return fallen.IsFailure ? fallen : Result.Success(FellBack(fallen.Value));
    }

    private static IMesh Produced(ManifoldOutcome outcome) =>
        outcome.Mesh.WithMetadata(outcome.Mesh.Metadata.WithCreatedBy(
            outcome.Provenance == ManifoldProvenance.NativeAfterMergingOperands ? NativeMergedProducer : NativeProducer));

    private static IMesh FellBack(IMesh mesh) => mesh.WithMetadata(mesh.Metadata.WithCreatedBy(FallbackProducer));

    private delegate Result<ManifoldOutcome> NativeOperation(IMesh left, IMesh right, MeshMetadata metadata);

    private delegate Result<IMesh> FallbackOperation(IBooleans fallback, IMesh left, IMesh right);

    private Result<IMesh> Run(
        IMesh meshA,
        IMesh meshB,
        string operation,
        NativeOperation native,
        FallbackOperation fallback)
    {
        var operands = BooleanOperands.Validate(meshA, meshB);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        var metadata = BooleanOperands.DescribeResult(meshA, meshB, operation);
        var result = native(meshA, meshB, metadata);

        if (result.IsSuccess)
        {
            return Result.Success(Produced(result.Value));
        }

        if (_fallback is null || !ShouldFallBack(result.Error))
        {
            return Result.Failure<IMesh>(result.Error);
        }

        var fallen = fallback(_fallback, meshA, meshB);

        return fallen.IsFailure ? fallen : Result.Success(FellBack(fallen.Value));
    }

    /// <summary>
    /// Whether a native failure is one the managed kernel might still handle.
    ///
    /// Geometry the native kernel rejects, and an operation it could not complete, are both
    /// worth retrying: the managed BSP kernel accepts input Manifold will not. So is an
    /// unavailable native library, which is the ordinary case on a platform whose binaries
    /// do not ship - without this the library would simply not work there.
    ///
    /// Three failures are deliberately *not* retried, because falling back would hide
    /// something that needs to be seen: an operand with no geometry is the caller's mistake;
    /// a mesh returned in a layout this binding does not understand is our mistake; and a
    /// binding mismatch means the native library is not the version this code was written
    /// against. Substituting a kernel with a weaker guarantee in any of those cases would
    /// convert a diagnosable fault into silently degraded output.
    /// </summary>
    private static bool ShouldFallBack(Error error) =>
        error.Code is "Manifold.InvalidMesh"
            or "Manifold.OperationFailed"
            or "Manifold.Unavailable";
}
