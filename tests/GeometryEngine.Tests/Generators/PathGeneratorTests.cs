namespace GeometryEngine.Tests.Generators;

[Suite("Generators / tubes, arcs, paths")]
public sealed class PathGeneratorTests
{
    private static ImmutableArray<double> Radii(int count, double radius) => [.. Enumerable.Repeat(radius, count)];

    [Fact]
    public void A_straight_capped_tube_is_a_closed_prism_of_the_expected_volume()
    {
        var path = ImmutableArray.Create(Vec3.Zero, new Vec3(0, 0, 5), new Vec3(0, 0, 10));

        var tube = Fixtures.Engine.Generators.GenerateTube(new TubeSpec(path, Radii(3, 2), Segments: 64)).Value;

        // A 64-gon inscribed in a circle of radius 2, swept 10 units.
        var polygonArea = 0.5 * 64 * 4 * Math.Sin(2 * Math.PI / 64);
        Check.RelativelyClose(polygonArea * 10, Fixtures.VolumeOf(tube), 1e-9);
        Check.True(Fixtures.TopologyOf(tube).IsClean);
    }

    [Fact]
    public void An_uncapped_tube_is_open_at_both_ends()
    {
        var path = ImmutableArray.Create(Vec3.Zero, new Vec3(10, 0, 0));

        var tube = Fixtures.Engine.Generators.GenerateTube(new TubeSpec(path, Radii(2, 1), Segments: 12, Capped: false)).Value;

        Check.Equal(24, Fixtures.TopologyOf(tube).BoundaryEdgeCount);
    }

    [Fact]
    public void A_tube_around_a_bend_does_not_twist_or_cut_through_itself()
    {
        var arc = Fixtures.Engine.Generators.GenerateArc(10, Vec3.Zero, Vec3.UnitX, Vec3.UnitY, 24).Value;

        var tube = Fixtures.Engine.Generators.GenerateTube(new TubeSpec(arc, Radii(arc.Length, 2), Segments: 16)).Value;

        Check.True(Fixtures.TopologyOf(tube).IsWatertight);
        Check.Equal(0, Fixtures.Engine.Evaluators.CountSelfIntersections(tube).Value);
        Check.Greater(Fixtures.VolumeOf(tube), 0);
    }

    [Fact]
    public void A_repeated_path_point_is_skipped_rather_than_poisoning_the_tube()
    {
        var path = ImmutableArray.Create(Vec3.Zero, new Vec3(0, 0, 5), new Vec3(0, 0, 5), new Vec3(0, 0, 10));

        var tube = Fixtures.Engine.Generators.GenerateTube(new TubeSpec(path, Radii(4, 1)));

        Check.True(tube.IsSuccess);
        Check.True(Fixtures.TopologyOf(tube.Value).IsWatertight);
    }

    [Fact]
    public void Tube_arguments_are_validated()
    {
        var path = ImmutableArray.Create(Vec3.Zero, Vec3.UnitZ);
        var generators = Fixtures.Engine.Generators;

        Check.Equal("Generators.PathTooShort", generators.GenerateTube(new TubeSpec([Vec3.Zero], [1])).Error.Code);
        Check.Equal("Generators.RadiiMismatch", generators.GenerateTube(new TubeSpec(path, [1])).Error.Code);
        Check.Equal("Generators.NonPositiveRadius", generators.GenerateTube(new TubeSpec(path, [1, -1])).Error.Code);
        Check.Equal("Generators.TooFewSegments", generators.GenerateTube(new TubeSpec(path, Radii(2, 1), Segments: 2)).Error.Code);
    }

    [Fact]
    public void An_arc_stays_on_its_bend_radius_and_ends_heading_the_new_way()
    {
        var arc = Fixtures.Engine.Generators.GenerateArc(5, new Vec3(1, 1, 1), Vec3.UnitX, Vec3.UnitZ, 20).Value;
        var centre = new Vec3(1, 1, 6);

        Check.Equal(21, arc.Length);
        foreach (var point in arc)
        {
            Check.Close(5, (point - centre).Length, 1e-9);
        }

        var heading = (arc[^1] - arc[^2]) / (arc[^1] - arc[^2]).Length;
        Check.Greater(heading.Dot(Vec3.UnitZ), 0.99);
    }

    [Fact]
    public void Directions_that_already_agree_need_no_bend()
    {
        var arc = Fixtures.Engine.Generators.GenerateArc(5, new Vec3(2, 0, 0), Vec3.UnitY, Vec3.UnitY, 20).Value;

        Check.Equal(1, arc.Length);
        Check.Equal(new Vec3(2, 0, 0), arc[0]);
    }

    [Fact]
    public void Resampling_a_path_evens_its_spacing_and_keeps_its_ends()
    {
        var path = ImmutableArray.Create(Vec3.Zero, new Vec3(0.1, 0, 0), new Vec3(0.2, 0, 0), new Vec3(10, 0.3, 0), new Vec3(20, 0, 0));

        var resampled = Fixtures.Engine.Generators.ResampleOpenPath(path, 1.0).Value;

        Check.Equal(path[0], resampled[0]);
        Check.Equal(path[^1], resampled[^1]);
        Check.Greater(resampled.Length, 15);
        for (var i = 1; i < resampled.Length; i++)
        {
            Check.Less((resampled[i] - resampled[i - 1]).Length, 1.5);
        }
    }

    [Fact]
    public void A_path_too_short_to_resample_comes_back_as_it_was()
    {
        var path = ImmutableArray.Create(Vec3.Zero, new Vec3(5, 0, 0));

        Check.True(Fixtures.Engine.Generators.ResampleOpenPath(path, 1).Value.SequenceEqual(path));
    }

    [Fact]
    public void A_draped_path_sits_sunk_into_the_surface_beneath_it()
    {
        var slab = Fixtures.Box(new Vec3(-20, -20, 0), new Vec3(20, 20, 5));
        var path = ImmutableArray.Create(new Vec3(-10, 0, 3), new Vec3(10, 0, 3));

        var draped = Fixtures.Engine.Generators.GenerateDrapedPath(
            new DrapedPathSpec(path, Radius: 2, Depth: 1, TopHeight: 8, Surface: Maybe<IMesh>.Some(slab))).Value;

        var stats = Fixtures.Engine.Evaluators.GetStatistics(draped).Value;
        Check.True(Fixtures.TopologyOf(draped).IsWatertight);
        Check.Close(4, stats.BoundsMin.Z, 1e-9);
        Check.Close(8, stats.BoundsMax.Z, 1e-9);
        Check.Close(-12, stats.BoundsMin.X, 0.05);
        Check.Close(2, stats.BoundsMax.Y, 0.05);
    }
}
