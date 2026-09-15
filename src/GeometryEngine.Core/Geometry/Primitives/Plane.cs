namespace GeometryEngine.Core.Geometry.Primitives;

/// <summary>Which side of a plane a point falls on.</summary>
public enum PointSide
{
    Coplanar = 0,
    Front = 1,
    Back = 2,
}

/// <summary>
/// An oriented plane, stored as a unit normal and the signed offset along it:
/// a point p lies on the plane when Normal . p == Offset.
/// A plane can only be built from non-degenerate input, so a "plane" with a
/// zero normal is unrepresentable.
/// </summary>
public sealed record Plane
{
    public Direction Normal { get; }
    public double Offset { get; }

    private Plane(Direction normal, double offset)
    {
        Normal = normal;
        Offset = offset;
    }

    /// <summary>Builds the plane through three points, wound counter-clockwise about the normal.</summary>
    public static Maybe<Plane> FromPoints(Vec3 a, Vec3 b, Vec3 c) =>
        Direction.From((b - a).Cross(c - a))
            .Map(normal => new Plane(normal, normal.Dot(a)));

    public static Plane FromNormalAndPoint(Direction normal, Vec3 point) =>
        new(normal, normal.Dot(point));

    /// <summary>Positive in front of the plane, negative behind it.</summary>
    public double SignedDistanceTo(Vec3 point) => Normal.Dot(point) - Offset;

    public PointSide Classify(Vec3 point, Tolerance tolerance)
    {
        var distance = SignedDistanceTo(point);
        if (tolerance.IsWithin(distance))
        {
            return PointSide.Coplanar;
        }

        return distance > 0 ? PointSide.Front : PointSide.Back;
    }

    /// <summary>The same geometric plane with the opposite orientation.</summary>
    public Plane Flipped() => new(Normal.Flipped(), -Offset);

    /// <summary>
    /// The point where the segment a-b crosses this plane. The caller is responsible
    /// for only asking when the endpoints straddle the plane.
    /// </summary>
    public Vec3 IntersectSegment(Vec3 a, Vec3 b)
    {
        var denominator = Normal.Dot(b - a);
        if (Math.Abs(denominator) < double.Epsilon)
        {
            return a;
        }

        var t = (Offset - Normal.Dot(a)) / denominator;
        return a.LerpTo(b, t);
    }

    public override string ToString() => $"[n={Normal}, d={Offset:0.######}]";
}
