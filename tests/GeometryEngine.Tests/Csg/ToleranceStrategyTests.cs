using GeometryEngine.Internal;

namespace GeometryEngine.Tests.Csg;

[Suite("Csg / tolerance strategy")]
public sealed class ToleranceStrategyTests
{
    [Fact]
    public void The_adaptive_tolerance_scales_with_the_combined_bounding_diagonal()
    {
        var big = Fixtures.Box(Vec3.Zero, new Vec3(10, 10, 10));
        var small = Fixtures.Box(Vec3.Zero, new Vec3(1, 1, 1));

        var tolerance = new AdaptiveTolerance(1e-7).For(big, small).Value;

        // Combined bounds span (0,0,0)-(10,10,10); the diagonal drives the tolerance.
        var expected = new Vec3(10, 10, 10).Length * 1e-7;
        Check.Close(expected, tolerance, expected * 1e-9);
    }

    [Fact]
    public void The_adaptive_tolerance_grows_in_proportion_to_the_model()
    {
        var unit = Fixtures.Box(Vec3.Zero, new Vec3(1, 1, 1));
        var tenfold = Fixtures.Box(Vec3.Zero, new Vec3(10, 10, 10));

        var small = new AdaptiveTolerance(1e-7).For(unit, unit).Value;
        var large = new AdaptiveTolerance(1e-7).For(tenfold, tenfold).Value;

        Check.Close(10.0, large / small, 1e-9);
    }

    [Fact]
    public void The_fixed_tolerance_ignores_the_operands()
    {
        var a = Fixtures.Box(Vec3.Zero, new Vec3(1, 1, 1));
        var b = Fixtures.Box(Vec3.Zero, new Vec3(1000, 1000, 1000));
        var chosen = Tolerance.From(1e-6).Value;

        Check.Equal(chosen, new FixedTolerance(chosen).For(a, b));
    }
}
