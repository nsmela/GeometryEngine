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
