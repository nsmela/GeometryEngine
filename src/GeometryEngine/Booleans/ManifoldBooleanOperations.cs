using GeometryEngine.Core.Common;
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
            var producer = result.Value.Provenance == ManifoldProvenance.NativeAfterMergingOperands
                ? NativeMergedProducer
                : NativeProducer;

            return Result.Success(result.Value.Mesh.WithMetadata(
                result.Value.Mesh.Metadata.WithCreatedBy(producer)));
        }

        if (_fallback is null || !ShouldFallBack(result.Error))
        {
            return Result.Failure<IMesh>(result.Error);
        }

        var fallen = fallback(_fallback, meshA, meshB);

        return fallen.IsFailure
            ? fallen
            : Result.Success(fallen.Value.WithMetadata(
                fallen.Value.Metadata.WithCreatedBy(FallbackProducer)));
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
