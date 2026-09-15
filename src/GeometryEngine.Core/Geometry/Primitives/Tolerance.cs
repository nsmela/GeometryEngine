namespace GeometryEngine.Core.Geometry.Primitives;

/// <summary>
/// A positive distance below which two quantities are considered the same.
/// Modelled as a type so that a tolerance can never be confused with any other
/// double that happens to be floating around (no primitive obsession).
/// </summary>
public readonly record struct Tolerance
{
    /// <summary>Classification epsilon used when deciding which side of a plane a point lies on.</summary>
    public static readonly Tolerance Planar = new(1e-9);

    /// <summary>Distance below which two vertices are merged into one.</summary>
    public static readonly Tolerance Welding = new(1e-9);

    public double Value { get; }

    private Tolerance(double value) => Value = value;

    public static Maybe<Tolerance> From(double value) =>
        value > 0 && double.IsFinite(value)
            ? Maybe<Tolerance>.Some(new Tolerance(value))
            : Maybe<Tolerance>.None();

    public bool IsWithin(double magnitude) => Math.Abs(magnitude) <= Value;

    public override string ToString() => Value.ToString("0.#######e+0");
}
