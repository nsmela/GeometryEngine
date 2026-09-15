namespace GeometryEngine.Core.Common;

/// <summary>
/// A strongly-typed domain error. Errors are values, not exceptions: they are
/// compared, returned and pattern-matched like any other value object.
/// </summary>
public sealed record Error(string Code, string Description)
{
    /// <summary>Sentinel for "no error". Only ever valid on a success result.</summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    public override string ToString() => $"{Code}: {Description}";
}
