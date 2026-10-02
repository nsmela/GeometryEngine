namespace GeometryEngine.Core.Geometry.Primitives;

/// <summary>
/// A rotation about an axis through the origin, held as a unit quaternion (W the scalar part,
/// X, Y, Z the vector part - the convention System.Numerics and WPF share, so a caller holding
/// either converts component by component).
///
/// A reference type for the same reason as <see cref="Direction"/>: a struct's default would be
/// the zero quaternion, which rotates nothing into nothing. Every way of making one yields a
/// unit quaternion.
/// </summary>
public sealed record Rotation
{
    public static readonly Rotation Identity = new(1, 0, 0, 0);

    public double W { get; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    private Rotation(double w, double x, double y, double z) => (W, X, Y, Z) = (w, x, y, z);

    /// <summary>A turn of <paramref name="radians"/> about <paramref name="axis"/>, counter-clockwise looking down it.</summary>
    public static Rotation FromAxisAngle(Direction axis, double radians)
    {
        var (sin, cos) = Math.SinCos(radians / 2);
        var a = axis.Vector;
        return new Rotation(cos, a.X * sin, a.Y * sin, a.Z * sin);
    }

    /// <summary>
    /// The rotation a quaternion describes, normalised. None for the zero quaternion or a
    /// non-finite one, neither of which is a rotation.
    /// </summary>
    public static Maybe<Rotation> FromQuaternion(double w, double x, double y, double z)
    {
        var length = Math.Sqrt((w * w) + (x * x) + (y * y) + (z * z));
        return double.IsFinite(length) && length > 1e-12
            ? Maybe<Rotation>.Some(new Rotation(w / length, x / length, y / length, z / length))
            : Maybe<Rotation>.None();
    }

    /// <summary>
    /// The smallest rotation taking <paramref name="from"/> onto <paramref name="to"/>. Opposite
    /// directions have no single smallest one; that half-turn is taken about whichever axis is
    /// perpendicular to <paramref name="from"/> and least aligned with it, so the answer is still
    /// determined by the input.
    /// </summary>
    public static Rotation Between(Direction from, Direction to)
    {
        var a = from.Vector;
        var b = to.Vector;
        var dot = a.Dot(b);

        if (dot < -1 + 1e-12)
        {
            var reference = Math.Abs(a.X) < 0.6 ? Vec3.UnitX : Vec3.UnitY;
            var axis = a.Cross(reference).Normalize();
            return new Rotation(0, axis.X, axis.Y, axis.Z);
        }

        // The half-way quaternion: (1 + a.b, a x b) has twice the half-angle's cosine and sine
        // in the right proportion, so normalising it gives the rotation without a trigonometric call.
        var cross = a.Cross(b);
        return FromQuaternion(1 + dot, cross.X, cross.Y, cross.Z).Value;
    }

    /// <summary>This rotation followed by <paramref name="next"/>.</summary>
    public Rotation Then(Rotation next) =>
        new(
            (next.W * W) - (next.X * X) - (next.Y * Y) - (next.Z * Z),
            (next.W * X) + (next.X * W) + (next.Y * Z) - (next.Z * Y),
            (next.W * Y) - (next.X * Z) + (next.Y * W) + (next.Z * X),
            (next.W * Z) + (next.X * Y) - (next.Y * X) + (next.Z * W));

    /// <summary>The rotation undoing this one.</summary>
    public Rotation Inverse() => new(W, -X, -Y, -Z);

    /// <summary>Turns a point or a direction about the origin.</summary>
    public Vec3 Apply(Vec3 v)
    {
        // v + 2w (q x v) + 2 q x (q x v), the quaternion sandwich product without building it.
        var q = new Vec3(X, Y, Z);
        var t = q.Cross(v) * 2;
        return v + (t * W) + q.Cross(t);
    }

    public override string ToString() => $"[w={W:0.######}, x={X:0.######}, y={Y:0.######}, z={Z:0.######}]";
}
