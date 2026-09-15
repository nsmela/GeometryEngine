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
}
