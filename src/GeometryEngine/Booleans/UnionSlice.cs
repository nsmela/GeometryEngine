using GeometryEngine.Internal;
using GeometryEngine.Internal.Csg;

namespace GeometryEngine.Booleans;

/// <summary>Ask for the solid occupied by either operand.</summary>
public sealed record UnionRequest(IMesh Left, IMesh Right);

/// <summary>Everything inside A or inside B.</summary>
internal sealed class UnionHandler(IToleranceStrategy tolerance)
{
    private readonly IToleranceStrategy _tolerance = tolerance;

    public Result<IMesh> Handle(UnionRequest request)
    {
        var operands = BooleanOperands.Validate(request.Left, request.Right);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        var tolerance = _tolerance.For(request.Left, request.Right);
        var merged = CsgKernel.Union(
            MeshPolygons.From(request.Left),
            MeshPolygons.From(request.Right),
            tolerance);

        return MeshHealer.Build(merged, BooleanOperands.DescribeResult(request.Left, request.Right, "Union"), tolerance);
    }
}
