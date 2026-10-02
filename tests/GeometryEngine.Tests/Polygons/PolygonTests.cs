namespace GeometryEngine.Tests.Polygons;

[Suite("Polygons")]
public sealed class PolygonTests
{
    private static IPolygonOperations Polygons => Fixtures.Engine.Polygons;

    [Fact]
    public void Offsetting_a_square_outwards_rounds_its_corners()
    {
        var square = PlanarPolygon.FromOuter(Assets.Square(0, 0, 10));

        var grown = Polygons.Offset(square, 1).Value;

        // The square, a strip along each side, and a quarter disc at each corner.
        Check.RelativelyClose(100 + 40 + Math.PI, Assets.AreaOf(grown), 0.005);
    }

    [Fact]
    public void An_offset_grows_an_outline_whichever_way_it_is_wound()
    {
        var clockwise = PlanarPolygon.FromOuter(Assets.Square(0, 0, 10, clockwise: true));

        Check.Greater(Assets.AreaOf(Polygons.Offset(clockwise, 1).Value), 100);
        Check.Less(Assets.AreaOf(Polygons.Offset(clockwise, -1).Value), 100);
    }

    [Fact]
    public void An_inset_that_splits_an_outline_keeps_the_largest_island()
    {
        // A dumbbell: a small square and a large rectangle joined by a neck two units wide, which
        // a 1.5 inset pinches off.
        var dumbbell = PlanarPolygon.FromOuter([
            new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 4), new Vec2(14, 4), new Vec2(14, -2), new Vec2(30, -2),
            new Vec2(30, 12), new Vec2(14, 12), new Vec2(14, 6), new Vec2(10, 6), new Vec2(10, 10), new Vec2(0, 10)]);

        var inset = Polygons.Offset(dumbbell, -1.5).Value;

        Check.Greater(inset.Outer.Min(p => p.X), 13);
    }

    [Fact]
    public void A_single_point_buffers_into_a_disc()
    {
        var disc = Polygons.BufferPath([new Vec2(3, 4)], 2).Value;

        Check.RelativelyClose(Math.PI * 4, Assets.AreaOf(disc), 0.01);
    }

    [Fact]
    public void A_buffered_path_is_a_stadium()
    {
        var stadium = Polygons.BufferPath([Vec2.Zero, new Vec2(10, 0)], 1).Value;

        Check.RelativelyClose(20 + Math.PI, Assets.AreaOf(stadium), 0.01);
    }

    [Fact]
    public void Buffering_needs_a_positive_distance_and_a_point()
    {
        Check.Equal("Polygons.NonPositiveDistance", Polygons.BufferPath([Vec2.Zero], 0).Error.Code);
        Check.Equal("Polygons.EmptyPath", Polygons.BufferPath([], 1).Error.Code);
    }

    [Fact]
    public void Overlapping_squares_union_into_one_outline()
    {
        var union = Polygons.Union([
            PlanarPolygon.FromOuter(Assets.Square(0, 0, 10)),
            PlanarPolygon.FromOuter(Assets.Square(5, 5, 10, clockwise: true))]).Value;

        Check.Close(175, Assets.AreaOf(union), 1e-6);
    }

    [Fact]
    public void Extruding_a_square_with_a_hole_gives_a_closed_frame()
    {
        var frame = new PlanarPolygon(Assets.Square(0, 0, 10), [Assets.Square(3, 3, 4)]);

        var solid = Polygons.Extrude(frame, 1, 3).Value;

        Check.Close((100 - 16) * 2, Fixtures.VolumeOf(solid), 1e-9);
        Check.True(Fixtures.TopologyOf(solid).IsWatertight);

        var stats = Fixtures.Engine.Evaluators.GetStatistics(solid).Value;
        Check.Close(1, stats.BoundsMin.Z, 1e-12);
        Check.Close(3, stats.BoundsMax.Z, 1e-12);
    }

    [Fact]
    public void Extrusion_ignores_the_winding_it_is_handed()
    {
        var clockwise = PlanarPolygon.FromOuter(Assets.Square(0, 0, 2, clockwise: true));

        Check.Close(8, Fixtures.VolumeOf(Polygons.Extrude(clockwise, 0, 2).Value), 1e-9);
    }

    [Fact]
    public void An_extrusion_needs_its_top_above_its_bottom()
    {
        Check.Equal("Polygons.InvertedExtrusion", Polygons.Extrude(PlanarPolygon.FromOuter(Assets.Square(0, 0, 1)), 2, 1).Error.Code);
    }

    [Fact]
    public void Mirroring_reflects_an_outline_and_keeps_its_orientation()
    {
        var polygon = new PlanarPolygon(Assets.Square(1, 0, 4), [Assets.Square(2, 1, 1, clockwise: true)]);

        var mirrored = Polygons.MirrorX(polygon);

        Check.Close(-5, mirrored.Outer.Min(p => p.X), 1e-12);
        Check.Greater(mirrored.SignedArea, 0);
        Check.Less(PlanarPolygon.SignedAreaOf(mirrored.Holes[0]), 0);
    }

    [Fact]
    public void A_triangulation_covers_an_outline_less_its_hole()
    {
        var triangulation = Polygons.Triangulate([new PlanarPolygon(Assets.Square(0, 0, 10), [Assets.Square(2, 2, 3)])]).Value;

        var area = 0.0;
        for (var i = 0; i < triangulation.Triangles.Length; i += 3)
        {
            var a = triangulation.Points[triangulation.Triangles[i]];
            var b = triangulation.Points[triangulation.Triangles[i + 1]];
            var c = triangulation.Points[triangulation.Triangles[i + 2]];
            var signed = (b - a).Cross(c - a) / 2;

            Check.Greater(signed, 0);
            area += signed;
        }

        Check.Close(91, area, 1e-9);
    }

    [Fact]
    public void A_triangulation_with_holes_is_delaunay_throughout()
    {
        // Two holes in a scalloped outline: enough edges to flip that the old limit - a dozen
        // flips in all, one per rebuild of the edge map - left some of them failing the test.
        static ImmutableArray<Vec2> Ring(int n, double outer, double inner, double cx, double cy, bool clockwise) =>
            [.. Enumerable.Range(0, n).Select(i =>
            {
                var angle = (clockwise ? -1 : 1) * 2 * Math.PI * i / n;
                var radius = i % 2 == 0 ? outer : inner;
                return new Vec2(cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle)));
            })];

        var polygon = new PlanarPolygon(
            Ring(80, 20, 18, 0, 0, clockwise: false),
            [Ring(20, 3, 2, -8, 0, clockwise: true), Ring(24, 3, 2.5, 8, 1, clockwise: true)]);

        var triangulation = Polygons.Triangulate([polygon]).Value;
        var points = triangulation.Points;
        var corners = triangulation.Triangles;

        var owners = new Dictionary<(int, int), List<int>>();
        for (var t = 0; t < corners.Length; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var (u, v) = (corners[t + k], corners[t + ((k + 1) % 3)]);
                var key = u < v ? (u, v) : (v, u);
                if (!owners.TryGetValue(key, out var list))
                {
                    owners[key] = list = [];
                }

                list.Add(t);
            }
        }

        int Opposite(int t, (int A, int B) edge) =>
            new[] { corners[t], corners[t + 1], corners[t + 2] }.First(v => v != edge.A && v != edge.B);

        static double Cross(Vec2 a, Vec2 b, Vec2 c) => (b - a).Cross(c - a);

        var failing = 0;
        foreach (var (edge, triangles) in owners)
        {
            if (triangles.Count != 2)
            {
                continue;
            }

            var (p, q) = (points[edge.Item1], points[edge.Item2]);
            var (r, s) = (points[Opposite(triangles[0], edge)], points[Opposite(triangles[1], edge)]);

            // s inside the circle through p, q, r - and the quad convex, so a flip was possible.
            var (a, b, c) = Cross(p, q, r) < 0 ? (p, r, q) : (p, q, r);
            double ax = a.X - s.X, ay = a.Y - s.Y, bx = b.X - s.X, by = b.Y - s.Y, cx = c.X - s.X, cy = c.Y - s.Y;
            var determinant =
                (((ax * ax) + (ay * ay)) * ((bx * cy) - (cx * by))) -
                (((bx * bx) + (by * by)) * ((ax * cy) - (cx * ay))) +
                (((cx * cx) + (cy * cy)) * ((ax * by) - (bx * ay)));

            if (determinant > 1e-12 && Cross(r, p, s) > 0 && Cross(r, s, q) > 0)
            {
                failing++;
            }
        }

        Check.Equal(0, failing);
    }

    [Fact]
    public void Collinear_points_along_an_edge_never_become_a_sliver_triangle()
    {
        // Outlines are subdivided so they can bend over a surface, which lines points up along
        // every straight stroke. The bold Z's diagonal, with its cap triangulated from it.
        var z = PlanarPolygon.FromOuter([
            new Vec2(-2.3451884, 1.585774), new Vec2(0.25313795, 1.585774), new Vec2(-2.5167365, -1.7322178),
            new Vec2(-2.5167365, -3), new Vec2(2.5167365, -3), new Vec2(2.5167365, -1.585774),
            new Vec2(-0.19456087, -1.585774), new Vec2(2.4288702, 1.7405858), new Vec2(2.4288702, 3),
            new Vec2(-2.3451884, 3)]);
        var spec = new DecalPrismSpec([z], new SurfaceFrame(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ), 0.8, -0.05, 0.05, MaxEdgeLength: 0.5);

        var prism = Fixtures.Engine.Decals.BuildPrism(spec).Value;

        for (var t = 0; t < prism.TriangleCount; t++)
        {
            var (a, b, c) = prism.TriangleAt(t);
            Check.Greater((b - a).Cross(c - a).Length / 2, 1e-6);
        }

        Check.True(Fixtures.TopologyOf(prism).IsWatertight);
    }

    [Fact]
    public void The_convex_hull_of_points_is_exact_and_counter_clockwise()
    {
        // A square's corners, an interior point and a point on an edge: neither of the last two
        // is a corner of the hull.
        var hull = Polygons.ConvexHull([
            new Vec2(10, 10), new Vec2(0, 0), new Vec2(5, 5), new Vec2(10, 0), new Vec2(5, 0), new Vec2(0, 10)]).Value;

        Check.Equal(4, hull.Outer.Length);
        Check.Close(100, hull.SignedArea, 1e-12);
    }

    [Fact]
    public void Points_spanning_no_area_have_no_hull()
    {
        Check.Equal("Polygons.DegenerateHull", Polygons.ConvexHull([new Vec2(1, 1)]).Error.Code);
        Check.Equal("Polygons.DegenerateHull", Polygons.ConvexHull([Vec2.Zero, new Vec2(1, 1), new Vec2(2, 2)]).Error.Code);
        Check.Equal("Polygons.DegenerateHull", Polygons.ConvexHull([]).Error.Code);
    }

    [Fact]
    public void Overlapping_squares_intersect_in_their_shared_corner()
    {
        var shared = Polygons.Intersect(
            PlanarPolygon.FromOuter(Assets.Square(0, 0, 10)),
            PlanarPolygon.FromOuter(Assets.Square(5, 5, 10, clockwise: true))).Value;

        Check.Equal(1, shared.Length);
        Check.Close(25, shared[0].Area, 1e-6);
    }

    [Fact]
    public void Polygons_that_do_not_overlap_intersect_in_nothing()
    {
        var shared = Polygons.Intersect(
            PlanarPolygon.FromOuter(Assets.Square(0, 0, 1)),
            PlanarPolygon.FromOuter(Assets.Square(5, 5, 1))).Value;

        Check.Equal(0, shared.Length);
    }

    [Fact]
    public void An_intersection_keeps_every_island_and_respects_holes()
    {
        // A bar across a frame: it crosses the frame's wall twice and its hole once, so two
        // islands, and the hole's span is missing from both.
        var frame = new PlanarPolygon(Assets.Square(0, 0, 10), [Assets.Square(2, 2, 6)]);
        var bar = PlanarPolygon.FromOuter([new Vec2(-1, 4), new Vec2(11, 4), new Vec2(11, 6), new Vec2(-1, 6)]);

        var shared = Polygons.Intersect(frame, bar).Value;

        Check.Equal(2, shared.Length);
        Check.Close(8, shared.Sum(p => p.Area), 1e-6);
    }

    [Fact]
    public void Subtracting_a_polygon_from_inside_another_leaves_a_hole()
    {
        var frame = Polygons.Subtract(
            PlanarPolygon.FromOuter(Assets.Square(0, 0, 10)),
            PlanarPolygon.FromOuter(Assets.Square(3, 3, 4))).Value;

        Check.Equal(1, frame.Length);
        Check.Equal(1, frame[0].Holes.Length);
        Check.Close(84, frame[0].Area, 1e-6);
    }

    [Fact]
    public void Subtracting_a_covering_polygon_leaves_nothing()
    {
        var left = Polygons.Subtract(
            PlanarPolygon.FromOuter(Assets.Square(1, 1, 2)),
            PlanarPolygon.FromOuter(Assets.Square(0, 0, 10))).Value;

        Check.Equal(0, left.Length);
    }

    [Fact]
    public void Loops_group_into_outlines_holes_and_islands_whatever_their_order()
    {
        // An "O" with a dot in its counter, handed over innermost first and all wound the same way.
        var dot = Assets.Square(4, 4, 2);
        var counter = Assets.Square(2, 2, 6);
        var ring = Assets.Square(0, 0, 10);

        var polygons = Polygons.FromLoops([dot, counter, ring]);

        Check.Equal(2, polygons.Length);

        var o = polygons.Single(p => p.Holes.Length == 1);
        Check.Close(64, o.Area, 1e-12);
        Check.Greater(o.SignedArea, 0);
        Check.Less(PlanarPolygon.SignedAreaOf(o.Holes[0]), 0);

        Check.Close(4, polygons.Single(p => p.Holes.Length == 0).Area, 1e-12);
    }

    [Fact]
    public void Loops_with_no_area_are_dropped()
    {
        var polygons = Polygons.FromLoops([[Vec2.Zero, new Vec2(1, 1), new Vec2(2, 2)], [Vec2.Zero, Vec2.Zero]]);

        Check.Equal(0, polygons.Length);
    }

    [Fact]
    public void Slicing_a_box_gives_its_footprint()
    {
        var slice = Polygons.Slice(Fixtures.Box(new Vec3(1, 2, 0), new Vec3(5, 4, 3)), 1.5).Value;

        Check.Equal(1, slice.Length);
        Check.Close(8, slice[0].Area, 1e-12);
        Check.Greater(slice[0].SignedArea, 0);
    }

    [Fact]
    public void Slicing_a_sphere_off_centre_gives_the_smaller_circle()
    {
        // At height 3 on a radius-5 sphere the circle has radius 4. The faceted sphere sits just
        // inside its true surface, so the area comes in slightly under.
        var slice = Polygons.Slice(Fixtures.Sphere(Vec3.Zero, 5, 96), 3).Value;

        Check.Equal(1, slice.Length);
        Check.RelativelyClose(Math.PI * 16, slice[0].Area, 0.01);
    }

    [Fact]
    public void Slicing_a_hollow_solid_gives_its_cavity_as_a_hole()
    {
        var hollow = Fixtures.Engine.Booleans.Subtract(Fixtures.Cube(0, 10), Fixtures.Cube(3, 4)).Value;

        var slice = Polygons.Slice(hollow, 5).Value;

        Check.Equal(1, slice.Length);
        Check.Equal(1, slice[0].Holes.Length);
        Check.Close(100 - 16, slice[0].Area, 1e-9);
    }

    [Fact]
    public void Separate_solids_slice_into_separate_polygons()
    {
        var pair = Fixtures.Engine.Booleans.Union([
            Fixtures.Box(Vec3.Zero, new Vec3(2, 2, 2)),
            Fixtures.Box(new Vec3(5, 0, 0), new Vec3(7, 2, 2))]).Value;

        var slice = Polygons.Slice(pair, 1).Value;

        Check.Equal(2, slice.Length);
        Check.Close(8, slice.Sum(p => p.Area), 1e-12);
    }

    [Fact]
    public void A_plane_that_misses_the_mesh_slices_nothing()
    {
        Check.Equal(0, Polygons.Slice(Fixtures.UnitCube(), 5).Value.Length);
    }

    [Fact]
    public void A_mesh_that_repeats_its_corners_per_triangle_still_closes()
    {
        // Every triangle with its own three vertices, as an unwelded STL arrives.
        var cube = Fixtures.UnitCube();
        var soup = ImmutableMesh.Create(
            [.. Enumerable.Range(0, cube.TriangleCount).SelectMany(t => { var (a, b, c) = cube.TriangleAt(t); return new[] { a, b, c }; })],
            [.. Enumerable.Range(0, cube.TriangleCount * 3)],
            MeshMetadata.Named("soup")).Value;

        var slice = Polygons.Slice(soup, 0.5).Value;

        Check.Equal(1, slice.Length);
        Check.Close(1, slice[0].Area, 1e-12);
    }

    [Fact]
    public void A_face_resting_on_the_plane_is_not_cut()
    {
        // The plane at exactly the box's top: the top face lies in it, and nothing is above it.
        Check.Equal(0, Polygons.Slice(Fixtures.UnitCube(), 1).Value.Length);
    }

    [Fact]
    public void The_convex_hull_of_a_spheres_shadow_is_nearly_its_disc()
    {
        var hull = Polygons.ProjectConvexHull(Fixtures.Sphere(Vec3.Zero, 20, 64)).Value;

        Check.RelativelyClose(Math.PI * 400, Assets.AreaOf(hull), 0.02);
    }

    [Fact]
    public void The_outline_of_an_l_shaped_part_keeps_its_notch()
    {
        // A convex hull would fill the notch in; the concave outline must not. The outline is
        // tuned for scanned surfaces, whose vertices sit a millimetre or so apart, so the part is
        // tessellated that finely rather than being a box of eight corners.
        var l = FlatGrid((x, y) => x < 10 || y < 10, 40);

        var outline = Polygons.ProjectOutline(l).Value;
        var hull = Polygons.ProjectConvexHull(l).Value;

        Check.RelativelyClose(700, Assets.AreaOf(outline), 0.05);
        Check.Greater(Assets.AreaOf(hull), 1000);
    }

    /// <summary>A flat sheet of unit cells over the part of a square the predicate keeps.</summary>
    private static IMesh FlatGrid(Func<int, int, bool> keep, int size)
    {
        var vertices = ImmutableArray.CreateBuilder<Vec3>();
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                vertices.Add(new Vec3(x, y, 0));
            }
        }

        var triangles = ImmutableArray.CreateBuilder<int>();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!keep(x, y))
                {
                    continue;
                }

                var corner = (y * (size + 1)) + x;
                triangles.AddRange(ImmutableArray.Create(corner, corner + 1, corner + size + 2, corner, corner + size + 2, corner + size + 1));
            }
        }

        return Fixtures.Engine.Modifiers.Repair(
            Fixtures.Engine.CreateMesh(vertices.ToImmutable(), triangles.ToImmutable(), MeshMetadata.Named("grid")).Value).Value;
    }
}
