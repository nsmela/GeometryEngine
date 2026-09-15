using GeometryEngine.Internal;
using GeometryEngine.Internal.Csg;

namespace GeometryEngine.Booleans;

/// <summary>Ask for the part of <see cref="Left"/> that <see cref="Right"/> does not occupy.</summary>
public sealed record SubtractRequest(IMesh Left, IMesh Right);

/// <summary>Everything inside A that is not inside B.</summary>
internal sealed class SubtractHandler(IToleranceStrategy tolerance)
{
    private readonly IToleranceStrategy _tolerance = tolerance;

    public Result<IMesh> Handle(SubtractRequest request)
    {
        var operands = BooleanOperands.Validate(request.Left, request.Right);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        var tolerance = _tolerance.For(request.Left, request.Right);
        var carved = CsgKernel.Subtract(
            MeshPolygons.From(request.Left),
            MeshPolygons.From(request.Right),
            tolerance);

        return MeshHealer.Build(carved, BooleanOperands.DescribeResult(request.Left, request.Right, "Subtract"), tolerance);
    }
}
