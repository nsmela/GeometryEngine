namespace GeometryEngine.Core.Geometry;

/// <summary>
/// A local tangent frame sitting on a surface: an origin on it, a tangent <see cref="U"/>
/// along which a label's baseline runs, a bitangent <see cref="V"/> pointing up the label,
/// and the outward normal <see cref="N"/>. The decal slices lay planar outlines into it.
/// </summary>
public sealed record SurfaceFrame(Vec3 Origin, Vec3 U, Vec3 V, Vec3 N)
{
    // Past this, a normal is close enough to vertical that world Z no longer pins down a
    // horizontal baseline, and world Y takes over as the reference.
    private const double NearlyVertical = 0.85;

    /// <summary>
    /// The frame a label placed at <paramref name="origin"/>, facing out along
    /// <paramref name="normal"/>, is laid into.
    ///
    /// Unrotated, the baseline <see cref="U"/> runs horizontally - perpendicular to world Z - so
    /// text on a wall reads level. On a surface facing nearly straight up or down there is no
    /// horizontal to speak of, and the baseline is taken perpendicular to world Y instead, so it
    /// runs along X. <paramref name="rotationRadians"/> then turns the baseline about the normal,
    /// counter-clockwise looking down it. A zero or non-finite normal is taken to face up.
    /// </summary>
    public static SurfaceFrame FromNormal(Vec3 origin, Vec3 normal, double rotationRadians = 0)
    {
        var n = normal.IsFinite && normal.LengthSquared > 1e-12 ? normal.Normalize() : Vec3.UnitZ;

        var reference = Math.Abs(n.Z) > NearlyVertical ? Vec3.UnitY : Vec3.UnitZ;
        var u = reference.Cross(n).Normalize();
        var v = n.Cross(u);

        if (rotationRadians != 0)
        {
            // u and v are perpendicular to n, so turning them about it is a rotation in their plane.
            var (sin, cos) = Math.SinCos(rotationRadians);
            (u, v) = ((u * cos) + (v * sin), (v * cos) - (u * sin));
        }

        return new SurfaceFrame(origin, u, v, n);
    }

    /// <summary>World position of local (u, v, height-above-surface) coordinates.</summary>
    public Vec3 ToWorld(double u, double v, double height) => Origin + (U * u) + (V * v) + (N * height);

    /// <summary>Local (u, v, height-above-surface) coordinates of a world position.</summary>
    public Vec3 ToLocal(Vec3 world)
    {
        var offset = world - Origin;
        return new Vec3(offset.Dot(U), offset.Dot(V), offset.Dot(N));
    }
}
