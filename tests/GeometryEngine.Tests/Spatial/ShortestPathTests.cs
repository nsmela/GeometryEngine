namespace GeometryEngine.Tests.Spatial;

[Suite("Spatial / shortest path")]
public sealed class ShortestPathTests
{
    private static ISpatialQueries Spatial => Fixtures.Engine.Spatial;

    private static double LengthOf(ImmutableArray<Vec3> path)
    {
        var length = 0.0;
        for (var i = 1; i < path.Length; i++)
        {
            length += path[i - 1].DistanceTo(path[i]);
        }

        return length;
    }

    [Fact]
    public void Across_one_flat_face_it_is_a_straight_line()
    {
        // The top of a box is two triangles, so this crosses the diagonal between them.
        var box = Fixtures.Box(Vec3.Zero, new Vec3(10, 10, 10));
        var (from, to) = (new Vec3(1, 2, 10), new Vec3(9, 7, 10));

        var path = Spatial.ShortestPath(box, from, to).Value;

        Check.Close(from.DistanceTo(to), LengthOf(path), 1e-9);
    }

    [Fact]
    public void Over_an_edge_it_is_the_straight_line_across_the_two_faces_laid_flat()
    {
        // The top face, and the front face folded up level with it: the end lands half a unit beyond
        // the edge the two share.
        var cube = Fixtures.Box(Vec3.Zero, new Vec3(1, 1, 1));
        var (from, to) = (new Vec3(0.2, 0.5, 1), new Vec3(0.8, 0, 0.5));

        var path = Spatial.ShortestPath(cube, from, to).Value;

        Check.Close(Math.Sqrt((0.6 * 0.6) + (1.0 * 1.0)), LengthOf(path), 1e-9);
    }

    [Fact]
    public void On_a_sphere_it_follows_the_great_circle()
    {
        // A quarter of a meridian. The facets lie inside the sphere, so the path is a little short
        // of the arc, and never shorter than the chord.
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 48);

        var length = LengthOf(Spatial.ShortestPath(sphere, new Vec3(5, 0, 0), new Vec3(0, 0, 5)).Value);

        Check.RelativelyClose(5 * Math.PI / 2, length, 0.02);
        Check.Greater(length, 5 * Math.Sqrt(2));
    }

    [Fact]
    public void On_a_finely_divided_sphere_it_is_the_great_circle_to_a_tenth_of_a_percent()
    {
        // A fine regular mesh is where a search over faces goes wrong: stepping centroid to centroid
        // it finds a staircase, and a path pulled taut along a staircase is still 8% too long here.
        var sphere = Fixtures.Sphere(Vec3.Zero, 50, 128);
        var (from, to) = (new Vec3(50, 0, 0), new Vec3(-10, 20, 45));

        var length = LengthOf(Spatial.ShortestPath(sphere, from, to).Value);

        var arc = 50 * Math.Acos(from.Normalize().Dot(to.Normalize()));
        Check.RelativelyClose(arc, length, 0.001);
    }

    [Fact]
    public void Every_point_is_on_the_surface_and_the_ends_are_the_nearest_points_to_those_asked_for()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 32);
        var index = Spatial.IndexFor(sphere).Value;
        var (from, to) = (new Vec3(7, 1, 0), new Vec3(-1, 2, 6));

        var path = Spatial.ShortestPath(sphere, from, to).Value;

        Check.Close(0, path[0].DistanceTo(index.ClosestPoint(from).Value.Point), 1e-9);
        Check.Close(0, path[^1].DistanceTo(index.ClosestPoint(to).Value.Point), 1e-9);
        foreach (var point in path)
        {
            Check.Close(0, index.ClosestPoint(point).Value.Distance, 1e-9);
        }
    }

    [Fact]
    public void A_mesh_whose_triangles_each_carry_their_own_corners_still_joins_up()
    {
        var cube = Fixtures.Box(Vec3.Zero, new Vec3(1, 1, 1));
        var (from, to) = (new Vec3(0.2, 0.5, 1), new Vec3(0.8, 0, 0.5));

        var welded = LengthOf(Spatial.ShortestPath(cube, from, to).Value);
        var unwelded = LengthOf(Spatial.ShortestPath(Assets.Unwelded(cube), from, to).Value);

        Check.Close(welded, unwelded, 1e-9);
    }

    [Fact]
    public void Two_pieces_that_do_not_touch_have_no_path_between_them()
    {
        var pair = Assets.Concatenate(
            Fixtures.Box(Vec3.Zero, new Vec3(1, 1, 1)),
            Fixtures.Box(new Vec3(5, 0, 0), new Vec3(6, 1, 1)));

        Check.True(Spatial.ShortestPath(pair, new Vec3(0.5, 0.5, 1), new Vec3(5.5, 0.5, 1)).IsFailure);
    }

    [Fact]
    public void Two_points_on_one_face_are_joined_directly()
    {
        var box = Fixtures.Box(Vec3.Zero, new Vec3(10, 10, 10));

        var path = Spatial.ShortestPath(box, new Vec3(1, 1, 10), new Vec3(2, 1, 10)).Value;

        Check.Equal(2, path.Length);
    }
}
