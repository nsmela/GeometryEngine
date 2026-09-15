using GeometryEngine.Internal;
using GeometryEngine.Internal.Csg;

namespace GeometryEngine.Booleans;

/// <summary>Ask for the solid both operands occupy.</summary>
public sealed record IntersectRequest(IMesh Left, IMesh Right);

/// <summary>Everything inside both A and B.</summary>
internal sealed class IntersectHandler(IToleranceStrategy tolerance)
{
    private readonly IToleranceStrategy _tolerance = tolerance;

    public Result<IMesh> Handle(IntersectRequest request)
    {
        var operands = BooleanOperands.Validate(request.Left, request.Right);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        var tolerance = _tolerance.For(request.Left, request.Right);
        var shared = CsgKernel.Intersect(
            MeshPolygons.From(request.Left),
            MeshPolygons.From(request.Right),
            tolerance);

        return MeshHealer.Build(shared, BooleanOperands.DescribeResult(request.Left, request.Right, "Intersection"), tolerance);
    }
}
