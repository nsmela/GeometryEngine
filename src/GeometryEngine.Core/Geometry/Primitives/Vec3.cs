namespace GeometryEngine.Core.Geometry.Primitives;

/// <summary>
/// An immutable point or displacement in 3D space, in double precision.
/// The default value (0,0,0) is a legal vector, so this is a record struct.
/// Every operation returns a new value; nothing here can be mutated.
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = default;
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => a * s;
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Vec3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    public Vec3 Cross(Vec3 other) => new(
        (Y * other.Z) - (Z * other.Y),
        (Z * other.X) - (X * other.Z),
        (X * other.Y) - (Y * other.X));

    public double LengthSquared => Dot(this);

    public double Length => Math.Sqrt(LengthSquared);

    public double DistanceTo(Vec3 other) => (this - other).Length;
    public double DistanceSquared(Vec3 other) => (this - other).LengthSquared;

    public Vec3 Normalize() => this / Length;

    /// <summary>Component-wise minimum. Used to accumulate bounding boxes.</summary>
    public Vec3 ComponentMin(Vec3 other) => new(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Min(Z, other.Z));

    /// <summary>Component-wise maximum. Used to accumulate bounding boxes.</summary>
    public Vec3 ComponentMax(Vec3 other) => new(Math.Max(X, other.X), Math.Max(Y, other.Y), Math.Max(Z, other.Z));

    /// <summary>Linear interpolation towards <paramref name="other"/>.</summary>
    public Vec3 LerpTo(Vec3 other, double t) => this + ((other - this) * t);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public override string ToString() => $"({X:0.######}, {Y:0.######}, {Z:0.######})";
}
