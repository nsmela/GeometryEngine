namespace GeometryEngine.Core.Geometry.Primitives;

/// <summary>
/// A unit-length vector. It is a reference type on purpose: a struct would have a
/// zero-length default value, and a zero-length direction is an illegal state.
/// The only way to obtain one is through <see cref="From"/>, which refuses degenerate input.
/// </summary>
public sealed record Direction
{
    public static readonly Direction X = new(Vec3.UnitX);
    public static readonly Direction Y = new(Vec3.UnitY);
    public static readonly Direction Z = new(Vec3.UnitZ);

    public Vec3 Vector { get; }

    private Direction(Vec3 unitVector) => Vector = unitVector;

    /// <summary>Normalises a vector. Returns None for zero-length or non-finite input.</summary>
    public static Maybe<Direction> From(Vec3 vector)
    {
        if (!vector.IsFinite)
        {
            return Maybe<Direction>.None();
        }

        var length = vector.Length;
        return length < 1e-12
            ? Maybe<Direction>.None()
            : Maybe<Direction>.Some(new Direction(vector / length));
    }

    public double Dot(Vec3 vector) => Vector.Dot(vector);

    public Direction Flipped() => new(-Vector);

    public static implicit operator Vec3(Direction direction) => direction.Vector;

    public override string ToString() => Vector.ToString();
}
