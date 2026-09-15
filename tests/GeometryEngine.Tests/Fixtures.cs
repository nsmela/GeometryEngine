namespace GeometryEngine.Tests;

/// <summary>
/// Shared arrangement for the test suite. The engine is stateless and immutable,
/// so one instance can safely serve every test.
/// </summary>
public static class Fixtures
{
    public static readonly IGeometryEngine Engine = BspGeometryEngine.Create();

    public static IMesh Box(Vec3 min, Vec3 max) => Engine.Generators.GenerateBox(min, max).Value;

    public static IMesh Cube(double origin, double size) =>
        Box(new Vec3(origin, origin, origin), new Vec3(origin + size, origin + size, origin + size));

    public static IMesh UnitCube() => Cube(0, 1);

    public static IMesh Sphere(Vec3 centre, double radius, int segments = 48) =>
        Engine.Generators.GenerateSphere(centre, radius, segments).Value;

    public static IMesh Cylinder(Vec3 baseCentre, double radius, double height, int segments = 64) =>
        Engine.Generators.GenerateCylinder(baseCentre, radius, height, segments).Value;

    public static double VolumeOf(IMesh mesh) => Engine.Evaluators.GetStatistics(mesh).Value.Volume;

    public static TopologyValidation TopologyOf(IMesh mesh) => Engine.Evaluators.ValidateTopology(mesh).Value;

    /// <summary>Exact volume shared by two equal spheres whose centres are <paramref name="distance"/> apart.</summary>
    public static double SphereLensVolume(double radius, double distance) =>
        distance >= 2 * radius
            ? 0
            : Math.PI * Math.Pow((2 * radius) - distance, 2) * (distance + (4 * radius)) / 12.0;

    public static double SphereVolume(double radius) => 4.0 / 3.0 * Math.PI * radius * radius * radius;
}
