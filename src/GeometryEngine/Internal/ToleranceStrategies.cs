namespace GeometryEngine.Internal;

/// <summary>
/// Supplies the geometric tolerance a boolean operation should run at. Made a strategy
/// because the right tolerance is not a fixed number: it depends on the scale of the
/// operands. See <see cref="AdaptiveTolerance"/>.
/// </summary>
internal interface IToleranceStrategy
{
    Tolerance For(IMesh left, IMesh right);
}

/// <summary>A caller-chosen tolerance, used verbatim regardless of the operands.</summary>
internal sealed class FixedTolerance(Tolerance tolerance) : IToleranceStrategy
{
    private readonly Tolerance _tolerance = tolerance;

    public Tolerance For(IMesh left, IMesh right) => _tolerance;
}

/// <summary>
/// A tolerance scaled to the size of the operands.
///
/// A single fixed tolerance cannot serve meshes of every scale. The library default of
/// 1e-9 is far tighter than the precision of a real model: an STL stores coordinates as
/// 32-bit floats, whose spacing near a coordinate of 50 mm is already ~6e-6, so cut
/// points that ought to coincide differ by far more than 1e-9 and never weld - the
/// boolean result comes out riddled with cracks and is not watertight. Tying the
/// tolerance to the combined bounding diagonal (a small fraction of it) tracks the
/// precision the geometry actually carries, so coincident points weld and the result
/// closes up. Measured across the bolus test set, this is what turns non-watertight
/// results watertight.
/// </summary>
internal sealed class AdaptiveTolerance(double relativeFactor) : IToleranceStrategy
{
    /// <summary>
    /// A fraction of the bounding diagonal. Chosen to sit a little above 32-bit float
    /// spacing at the model's scale - loose enough to weld coincident cut points, tight
    /// enough not to merge genuinely distinct features.
    /// </summary>
    public const double DefaultFactor = 1e-7;

    private const double Floor = 1e-12;

    private readonly double _relativeFactor = relativeFactor;

    public Tolerance For(IMesh left, IMesh right)
    {
        var diagonal = CombinedDiagonal(left, right);
        var value = Math.Max(diagonal * _relativeFactor, Floor);
        return Tolerance.From(value).GetValueOrDefault(Tolerance.Welding);
    }

    private static double CombinedDiagonal(IMesh left, IMesh right)
    {
        var min = new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Vec3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

        Accumulate(left, ref min, ref max);
        Accumulate(right, ref min, ref max);

        return double.IsFinite(min.X) ? (max - min).Length : 0.0;
    }

    private static void Accumulate(IMesh mesh, ref Vec3 min, ref Vec3 max)
    {
        foreach (var vertex in mesh.Vertices)
        {
            min = min.ComponentMin(vertex);
            max = max.ComponentMax(vertex);
        }
    }
}
