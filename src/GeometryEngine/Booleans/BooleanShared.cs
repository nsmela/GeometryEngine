namespace GeometryEngine.Booleans;

/// <summary>Errors that any boolean slice can report.</summary>
internal static class BooleanErrors
{
    public static readonly Error EmptyOperand = MeshErrors.EmptyOperand;

    public static Error KernelFailure(string description) =>
        new("Booleans.KernelFailure", description);
}

/// <summary>
/// The precondition every boolean slice shares: two non-empty operands.
/// Kept in one place so the three slices state it once between them.
/// </summary>
internal static class BooleanOperands
{
    public static Result Validate(IMesh left, IMesh right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.IsEmpty || right.IsEmpty
            ? Result.Failure(BooleanErrors.EmptyOperand)
            : Result.Success();
    }

    /// <summary>
    /// Names a result after the operation that produced it, as Fabolus does. The result is a new
    /// mesh rather than either operand, so its annotations are whatever the left operand's survive
    /// a <see cref="MeshOperation.Combine"/> - the left one because that is the one the operation
    /// reads as the subject ("A minus B"), and usually nothing at all.
    /// </summary>
    public static MeshMetadata DescribeResult(IMesh left, IMesh right, string operation) =>
        new($"{left.Metadata.Name} {operation} {right.Metadata.Name}", "GeometryEngine.Booleans")
        {
            Annotations = left.Metadata.Annotations?.Carry(MeshOperation.Combine),
        };
}
