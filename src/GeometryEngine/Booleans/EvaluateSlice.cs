using GeometryEngine.Internal;

namespace GeometryEngine.Booleans;

/// <summary>Ask for the solid a description stands for.</summary>
public sealed record EvaluateRequest(Solid Query);

/// <summary>
/// Evaluates a description one step at a time through pairwise booleans. This is all the managed
/// kernel can do with one - it has no way to hold a half-built solid without writing it out as a
/// mesh - and it is what the native kernel falls back to for a description it declines.
///
/// A step may leave nothing: a solid less itself, or two that do not meet. The pairwise
/// operations refuse an empty operand, since for a caller's own mesh that is a mistake; here it
/// is an ordinary intermediate value, so the steps around it are answered by what an empty solid
/// means rather than by the kernel.
/// </summary>
internal sealed class EvaluateHandler(IBooleans pairwise)
{
    private readonly IBooleans _pairwise = pairwise;

    public Result<IMesh> Handle(EvaluateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Query);

        var query = request.Query;
        var leaves = SolidWalk.Leaves(query);
        var operands = BooleanOperands.Validate(leaves);
        if (operands.IsFailure)
        {
            return Result.Failure<IMesh>(operands.Error);
        }

        if (query is Solid.Leaf only)
        {
            return Result.Success(only.Mesh);
        }

        var built = new Dictionary<Solid, IMesh>(ReferenceEqualityComparer.Instance);
        foreach (var node in SolidWalk.PostOrder(query))
        {
            if (node is Solid.Leaf leaf)
            {
                built.Add(node, leaf.Mesh);
                continue;
            }

            var step = (Solid.Combined)node;
            var combined = Combine(step.Op, built[step.Left], built[step.Right]);
            if (combined.IsFailure)
            {
                return combined;
            }

            built.Add(node, combined.Value);
        }

        return Result.Success(built[query].WithMetadata(BooleanOperands.DescribeQuery(query, leaves)));
    }

    private Result<IMesh> Combine(BooleanOp op, IMesh left, IMesh right) =>
        (op, left.IsEmpty, right.IsEmpty) switch
        {
            // Nothing joined to a solid is the solid; nothing less anything, or met with
            // anything, is still nothing; and a solid less nothing is itself.
            (BooleanOp.Union, true, _) => Result.Success(right),
            (BooleanOp.Union, _, true) => Result.Success(left),
            (BooleanOp.Subtract, true, _) or (BooleanOp.Subtract, _, true) => Result.Success(left),
            (BooleanOp.Intersect, true, _) => Result.Success(left),
            (BooleanOp.Intersect, _, true) => Result.Success(right),

            (BooleanOp.Union, _, _) => _pairwise.Union(left, right),
            (BooleanOp.Subtract, _, _) => _pairwise.Subtract(left, right),
            (BooleanOp.Intersect, _, _) => _pairwise.Intersect(left, right),

            _ => Result.Failure<IMesh>(BooleanErrors.UnknownOperation(op)),
        };
}
