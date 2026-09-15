namespace GeometryEngine.Core.Geometry;

/// <summary>
/// A local tangent frame sitting on a surface: an origin on it, a tangent <see cref="U"/>
/// along which a label's baseline runs, a bitangent <see cref="V"/> pointing up the label,
/// and the outward normal <see cref="N"/>. The decal slices lay planar outlines into it.
/// </summary>
public sealed record SurfaceFrame(Vec3 Origin, Vec3 U, Vec3 V, Vec3 N)
{
    /// <summary>World position of local (u, v, height-above-surface) coordinates.</summary>
    public Vec3 ToWorld(double u, double v, double height) => Origin + (U * u) + (V * v) + (N * height);

    /// <summary>Local (u, v, height-above-surface) coordinates of a world position.</summary>
    public Vec3 ToLocal(Vec3 world)
    {
        var offset = world - Origin;
        return new Vec3(offset.Dot(U), offset.Dot(V), offset.Dot(N));
    }
}
