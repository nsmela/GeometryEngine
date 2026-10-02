namespace GeometryEngine.Tests.Evaluators;

[Suite("Evaluators / peaks")]
public sealed class PeakTests
{
    private static IGeometryEvaluators Evaluators => Fixtures.Engine.Evaluators;

    /// <summary>Two square towers on a shared slab, joined by a bridge between their tops.</summary>
    private static IMesh TwoTowers(double leftTop, double rightTop, double bridgeTop)
    {
        IMesh[] parts =
        [
            Fixtures.Box(new Vec3(0, 0, 0), new Vec3(40, 10, 2)),
            Fixtures.Box(new Vec3(0, 0, 0), new Vec3(10, 10, leftTop)),
            Fixtures.Box(new Vec3(30, 0, 0), new Vec3(40, 10, rightTop)),
            Fixtures.Box(new Vec3(5, 3, 0), new Vec3(35, 7, bridgeTop)),
        ];

        return Fixtures.Engine.Booleans.Union([.. parts]).Value;
    }

    [Fact]
    public void A_sphere_has_one_peak_at_its_top()
    {
        var peaks = Evaluators.FindPeaks(Fixtures.Sphere(new Vec3(1, 2, 3), 5, 24), Direction.Z).Value;

        var top = peaks.Peaks[0];
        Check.Close(8, top.Point.Z, 1e-9);
        Check.True(double.IsPositiveInfinity(top.Prominence));
        Check.Greater(top.Normal.Z, 0.9);
    }

    [Fact]
    public void The_lower_of_two_towers_is_as_prominent_as_its_drop_to_the_bridge()
    {
        var peaks = Evaluators.FindPeaks(TwoTowers(leftTop: 20, rightTop: 15, bridgeTop: 9), Direction.Z).Value;

        var upward = peaks.Peaks.Where(p => p.Normal.Z > 0).ToList();
        Check.Close(20, upward[0].Point.Z, 1e-9);
        Check.True(double.IsPositiveInfinity(upward[0].Prominence));
        Check.Close(15, upward[1].Point.Z, 1e-9);
        Check.Close(6, upward[1].Prominence, 1e-9);
    }

    [Fact]
    public void Peaks_are_found_along_whichever_direction_is_up()
    {
        // The same towers laid on their side: up is now +X, and the higher tower's top faces it.
        var lying = Fixtures.Engine.Transforms.Rotate(
            TwoTowers(leftTop: 20, rightTop: 15, bridgeTop: 9), Direction.Y, Math.PI / 2).Value;

        var peaks = Evaluators.FindPeaks(lying, Direction.X).Value;

        var upward = peaks.Peaks.Where(p => p.Normal.X > 0).ToList();
        Check.Close(20, upward[0].Point.X, 1e-9);
        Check.Close(6, upward[1].Prominence, 1e-9);
    }

    [Fact]
    public void A_point_on_the_peaks_cap_is_within_reach_and_one_on_another_tower_is_not()
    {
        var peaks = Evaluators.FindPeaks(TwoTowers(leftTop: 20, rightTop: 15, bridgeTop: 9), Direction.Z).Value;
        var lower = peaks.Peaks.First(p => p.Normal.Z > 0 && p.Point.Z == 15);

        // Each point is close by a corner of the tower it is on, so it is that corner it is judged by.
        var reach = peaks.WithinReach(lower, 3, [new Vec3(39, 1, 14.5), new Vec3(1, 1, 19.5), new Vec3(39, 1, 0.5)]);

        Check.True(reach[0]);   // on the lower tower, within 3 of its top
        Check.False(reach[1]);  // on the other tower
        Check.False(reach[2]);  // on the lower tower, but below the cap
    }

    [Fact]
    public void A_flat_top_is_marked_in_its_middle()
    {
        var peaks = Evaluators.FindPeaks(Fixtures.Box(new Vec3(0, 0, 0), new Vec3(10, 6, 4)), Direction.Z).Value;

        var (point, normal) = peaks.Summit(peaks.Peaks[0], 0.05);

        Check.Less(point.DistanceTo(new Vec3(5, 3, 4)), 1e-9);
        Check.Less(normal.DistanceTo(Vec3.UnitZ), 1e-12);
    }

    [Fact]
    public void A_mesh_that_repeats_its_corners_per_triangle_reads_as_one_surface()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 16);
        var soup = ImmutableMesh.Create(
            [.. Enumerable.Range(0, sphere.TriangleCount).SelectMany(t => { var (a, b, c) = sphere.TriangleAt(t); return new[] { a, b, c }; })],
            [.. Enumerable.Range(0, sphere.TriangleCount * 3)],
            MeshMetadata.Named("soup")).Value;

        var peaks = Evaluators.FindPeaks(soup, Direction.Z).Value;

        Check.Equal(1, peaks.Peaks.Count(p => p.Normal.Z > 0));
    }

    [Fact]
    public void An_empty_mesh_has_no_peaks_to_find()
    {
        Check.True(Evaluators.FindPeaks(ImmutableMesh.Empty, Direction.Z).IsFailure);
    }
}
