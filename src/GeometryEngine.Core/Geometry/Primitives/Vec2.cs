namespace GeometryEngine.Core.Geometry.Primitives;

/// <summary>
/// An immutable point or displacement in the plane, in double precision. The planar
/// counterpart of <see cref="Vec3"/>, used by the polygon and decal slices.
/// </summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = default;

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => a * s;
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public double Dot(Vec2 other) => (X * other.X) + (Y * other.Y);

    /// <summary>The z component of the 3D cross product: positive when <paramref name="other"/> turns counter-clockwise from this.</summary>
    public double Cross(Vec2 other) => (X * other.Y) - (Y * other.X);

    public double LengthSquared => Dot(this);

    public double Length => Math.Sqrt(LengthSquared);

    public double DistanceTo(Vec2 other) => (this - other).Length;

    public Vec2 LerpTo(Vec2 other, double t) => this + ((other - this) * t);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public override string ToString() => $"({X:0.######}, {Y:0.######})";
}
