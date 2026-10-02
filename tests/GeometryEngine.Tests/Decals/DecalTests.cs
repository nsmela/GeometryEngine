namespace GeometryEngine.Tests.Decals;

[Suite("Decals")]
public sealed class DecalTests
{
    private static readonly SurfaceFrame Flat = new(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);

    [Fact]
    public void A_prism_without_a_surface_stands_on_the_frames_plane()
    {
        var spec = new DecalPrismSpec([PlanarPolygon.FromOuter(Assets.Square(-2, -1, 4))], Flat, Depth: 1, Sink: -0.5, Overshoot: 0.25);

        var prism = Fixtures.Engine.Decals.BuildPrism(spec).Value;
        var stats = Fixtures.Engine.Evaluators.GetStatistics(prism).Value;

        Check.True(Fixtures.TopologyOf(prism).IsWatertight);
        Check.Close(16 * 1.75, stats.Volume, 1e-9);
        Check.Close(-0.5, stats.BoundsMin.Z, 1e-12);
        Check.Close(1.25, stats.BoundsMax.Z, 1e-12);
    }

    [Fact]
    public void A_glyph_with_a_hole_keeps_the_hole()
    {
        var o = new PlanarPolygon(Assets.Square(0, 0, 6), [Assets.Square(2, 2, 2)]);

        var prism = Fixtures.Engine.Decals.BuildPrism(new DecalPrismSpec([o], Flat, 1, 0, 0)).Value;

        Check.Close(32, Fixtures.VolumeOf(prism), 1e-9);
    }

    [Fact]
    public void A_label_on_a_flat_face_lies_flat_on_it()
    {
        // On a flat face every point already sits on the surface, and a ray settling it that
        // starts there misses that face - the points sank through to the face behind, and the
        // label on a mould's flat side came back shredded into spikes reaching its far wall.
        var slab = Fixtures.Box(new Vec3(-30, -30, -4), new Vec3(30, 30, 0));
        var spec = new DecalPrismSpec(
            [PlanarPolygon.FromOuter(Assets.Square(-10, -5, 10))],
            new SurfaceFrame(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ),
            Depth: 0.8, Sink: -0.05, Overshoot: 0.05, MaxEdgeLength: 1,
            Surface: Maybe<IMesh>.Some(slab));

        var prism = Fixtures.Engine.Decals.BuildPrism(spec).Value;

        for (var i = 0; i < prism.VertexCount; i += 2)
        {
            Check.Close(-0.05, prism.Vertices[i].Z, 1e-6);
            Check.Close(0.85, prism.Vertices[i + 1].Z, 1e-6);
        }
    }

    [Fact]
    public void A_glyph_with_holes_has_walls_only_along_its_outlines()
    {
        // The triangulator joins each hole to the outline with a slit, visiting the slit's two
        // ends twice. Those visits came back as separate points, so the prism took the slit for
        // an outline and stood a wall either side of it: a double wall inside the letter from
        // its edge to each counter, invisible on a closed solid but plain through a translucent
        // preview - one in an A, two in a B.
        var b = new PlanarPolygon(Assets.Square(0, 0, 10), [Assets.Square(2, 2, 2, true), Assets.Square(2, 6, 2, true)]);

        var prism = Fixtures.Engine.Decals.BuildPrism(new DecalPrismSpec([b], Flat, 1, 0, 0)).Value;

        // The builder interleaves bottom and top copies, so a wall triangle mixes odd and even corners.
        var t = prism.Triangles;
        var walls = Enumerable.Range(0, t.Length / 3)
            .Count(i => (t[i * 3] % 2) + (t[(i * 3) + 1] % 2) + (t[(i * 3) + 2] % 2) is 1 or 2);

        Check.Equal(2 * (4 + 4 + 4), walls);
        Check.Equal(3 * 4, prism.VertexCount / 2);

        // Both counters were bridged to the same corner, and joining the second at the wrong visit
        // of it overlapped the cap's triangles: a cap that covers the letter exactly once has
        // exactly its volume.
        Check.Close(100 - 8, Fixtures.VolumeOf(prism), 1e-9);
    }

    [Fact]
    public void A_prism_on_a_curved_surface_follows_it()
    {
        // A label wrapped around the side of a cylinder: every bottom vertex must sit on the
        // cylinder, not on a flat plane cutting across it.
        const double radius = 20;
        var cylinder = Fixtures.Cylinder(new Vec3(0, 0, -30), radius, 60, 256);
        var frame = new SurfaceFrame(new Vec3(0, -radius, 0), Vec3.UnitX, Vec3.UnitZ, -Vec3.UnitY);

        var spec = new DecalPrismSpec(
            [PlanarPolygon.FromOuter(Assets.Square(-12, -2, 24))],
            frame,
            Depth: 1,
            Sink: 0,
            Overshoot: 0,
            MaxEdgeLength: 1,
            Surface: Maybe<IMesh>.Some(cylinder));

        var prism = Fixtures.Engine.Decals.BuildPrism(spec).Value;

        var onSurface = prism.Vertices.Count(v => Math.Abs(Math.Sqrt((v.X * v.X) + (v.Y * v.Y)) - radius) < 0.05);
        Check.GreaterOrEqual(onSurface, prism.VertexCount / 2);
        Check.True(Fixtures.TopologyOf(prism).IsWatertight);
    }

    [Fact]
    public void Projecting_a_prism_lays_it_onto_the_surface_at_its_height()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 30, 96);
        var frame = new SurfaceFrame(new Vec3(0, 0, 30), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var prism = Fixtures.Engine.Decals.BuildPrism(
            new DecalPrismSpec([PlanarPolygon.FromOuter(Assets.Square(-5, -5, 10))], frame, Depth: 1, Sink: 0, Overshoot: 0)).Value;

        var projected = Fixtures.Engine.Decals.ProjectPrism(sphere, frame, prism).Value;

        Check.False(projected.ExtendsPastSurface);
        Check.False(projected.SurfaceTooCurved);

        // The frame's plane grazes the sphere only at its centre; after projection every bottom
        // vertex rides the surface and every top vertex sits one unit above it.
        foreach (var vertex in projected.Mesh.Vertices)
        {
            var height = vertex.Length - 30;
            Check.True(Math.Abs(height) < 0.05 || Math.Abs(height - 1) < 0.05);
        }
    }

    [Fact]
    public void A_prism_overhanging_the_surface_is_reported()
    {
        var small = Fixtures.Sphere(Vec3.Zero, 3, 48);
        var frame = new SurfaceFrame(new Vec3(0, 0, 3), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var prism = Fixtures.Engine.Decals.BuildPrism(
            new DecalPrismSpec([PlanarPolygon.FromOuter(Assets.Square(-10, -1, 20))], frame, 1, 0, 0)).Value;

        var projected = Fixtures.Engine.Decals.ProjectPrism(small, frame, prism).Value;

        Check.True(projected.ExtendsPastSurface);
    }

    [Fact]
    public void A_prebuilt_index_builds_the_same_prism_as_the_mesh_does()
    {
        // The reason a caller passes an index is that preparing one dominates the cost of a
        // prism, so the two routes have to agree exactly or the fast one is not the same feature.
        const double radius = 20;
        var cylinder = Fixtures.Cylinder(new Vec3(0, 0, -30), radius, 60, 256);
        var frame = new SurfaceFrame(new Vec3(0, -radius, 0), Vec3.UnitX, Vec3.UnitZ, -Vec3.UnitY);
        var outlines = new[] { PlanarPolygon.FromOuter(Assets.Square(-12, -2, 24)) }.ToImmutableArray();

        var fromMesh = Fixtures.Engine.Decals.BuildPrism(new DecalPrismSpec(
            outlines, frame, 1, 0, 0, MaxEdgeLength: 1, Surface: Maybe<IMesh>.Some(cylinder))).Value;

        using var index = Fixtures.Engine.Spatial.BuildIndex(cylinder).Value;
        var fromIndex = Fixtures.Engine.Decals.BuildPrism(new DecalPrismSpec(
            outlines, frame, 1, 0, 0, MaxEdgeLength: 1, SurfaceIndex: Maybe<ISpatialIndex>.Some(index))).Value;

        Check.Equal(fromMesh.VertexCount, fromIndex.VertexCount);
        for (var i = 0; i < fromMesh.VertexCount; i++)
        {
            Check.Close(0, (fromMesh.Vertices[i] - fromIndex.Vertices[i]).Length, 1e-12);
        }
    }

    [Fact]
    public void A_prebuilt_index_stays_usable_after_the_prism_is_built()
    {
        // The index is the caller's: building a prism against it must not close it, or the second
        // label on the same model would fail.
        var sphere = Fixtures.Sphere(Vec3.Zero, 30, 96);
        var frame = new SurfaceFrame(new Vec3(0, 0, 30), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var spec = new DecalPrismSpec(
            [PlanarPolygon.FromOuter(Assets.Square(-5, -5, 10))],
            frame, 1, 0, 0, MaxEdgeLength: 1, SurfaceIndex: Maybe<ISpatialIndex>.Some(
                Fixtures.Engine.Spatial.BuildIndex(sphere).Value));

        using var index = spec.SurfaceIndex.Value;

        Check.True(Fixtures.Engine.Decals.BuildPrism(spec).IsSuccess);
        Check.True(Fixtures.Engine.Decals.BuildPrism(spec).IsSuccess);
        Check.True(index.ClosestPoint(new Vec3(0, 0, 40)).HasValue);
    }

    [Fact]
    public void Projecting_through_a_prebuilt_index_matches_projecting_through_the_mesh()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 30, 96);
        var frame = new SurfaceFrame(new Vec3(0, 0, 30), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var prism = Fixtures.Engine.Decals.BuildPrism(
            new DecalPrismSpec([PlanarPolygon.FromOuter(Assets.Square(-5, -5, 10))], frame, 1, 0, 0)).Value;

        var viaMesh = Fixtures.Engine.Decals.ProjectPrism(sphere, frame, prism).Value;

        using var index = Fixtures.Engine.Spatial.BuildIndex(sphere).Value;
        var viaIndex = Fixtures.Engine.Decals.ProjectPrism(index, frame, prism).Value;

        Check.Equal(viaMesh.ExtendsPastSurface, viaIndex.ExtendsPastSurface);
        Check.Equal(viaMesh.SurfaceTooCurved, viaIndex.SurfaceTooCurved);
        Check.Equal(viaMesh.Mesh.VertexCount, viaIndex.Mesh.VertexCount);
        for (var i = 0; i < viaMesh.Mesh.VertexCount; i++)
        {
            Check.Close(0, (viaMesh.Mesh.Vertices[i] - viaIndex.Mesh.Vertices[i]).Length, 1e-12);
        }
    }

    [Fact]
    public void A_label_wrapped_onto_a_faceted_scan_keeps_its_cap_the_right_way_up()
    {
        // Each point used to settle onto the plane of its own nearest facet and stand its column
        // along that facet's normal. Neighbours either side of a facet edge then landed out of
        // order or leaned apart, the cap triangle between them turned over, and the label showed
        // holes through to the model - on most placements on a real scan, while every check of
        // the prism's topology still passed.
        var scan = Assets.LoadBench("scalp_bolus.stl");
        using var index = Fixtures.Engine.Spatial.BuildIndex(scan).Value;
        var outlines = GlyphLikeOutlines();

        var flat = Fixtures.Engine.Decals.BuildPrism(new DecalPrismSpec(outlines, Flat, 0.8, -0.05, 0.05, MaxEdgeLength: 1.25)).Value;

        var placements = 0;
        for (var x = -45.0; x <= 45; x += 15)
        {
            for (var y = -55.0; y <= -10; y += 15)
            {
                var hit = index.Raycast(new Vec3(x, y, 300), Direction.From(-Vec3.UnitZ).Value);
                if (!hit.HasValue)
                {
                    continue;
                }

                var normal = hit.Value.Normal.Z < 0 ? -hit.Value.Normal : hit.Value.Normal;
                if (normal.Z < 0.6)
                {
                    continue; // Too far down the side: the label would hang off the rim.
                }

                foreach (var degrees in new[] { 0.0, 37.0, 90.0 })
                {
                    var prism = Fixtures.Engine.Decals.BuildPrism(new DecalPrismSpec(
                        outlines, FrameAt(hit.Value.Point, normal, degrees), 0.8, -0.05, 0.05,
                        MaxEdgeLength: 1.25, SurfaceIndex: Maybe<ISpatialIndex>.Some(index))).Value;

                    AssertCapIsTheRightWayUp(prism, flat, $"at ({x}, {y}) turned {degrees} degrees");
                    placements++;
                }
            }
        }

        Check.GreaterOrEqual(placements, 30);
    }

    /// <summary>
    /// Every top cap triangle faces the way its columns rise. <paramref name="flat"/> is the same
    /// prism on a plane, with the same vertex order, so slivers can be told apart: their
    /// orientation is rounding noise, and they have no area to show a hole through.
    /// </summary>
    private static void AssertCapIsTheRightWayUp(IMesh prism, IMesh flat, string where)
    {
        const double minimumAltitude = 0.05;

        var v = prism.Vertices;
        var t = prism.Triangles;
        for (var i = 0; i < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];

            // The builder interleaves a bottom and a top copy of every point, so a triangle whose
            // corners are all odd is on the top cap.
            if (a % 2 == 0 || b % 2 == 0 || c % 2 == 0)
            {
                continue;
            }

            var (fa, fb, fc) = (flat.Vertices[a], flat.Vertices[b], flat.Vertices[c]);
            var longest = Math.Max((fb - fa).Length, Math.Max((fc - fb).Length, (fa - fc).Length));
            var planarArea = (fb - fa).Cross(fc - fa).Length / 2;
            if (2 * planarArea / longest < minimumAltitude)
            {
                continue;
            }

            var rise = (v[a] - v[a - 1]) + (v[b] - v[b - 1]) + (v[c] - v[c - 1]);
            var facing = (v[b] - v[a]).Cross(v[c] - v[a]);
            Check.True(facing.Dot(rise) > 0, $"A cap triangle of area {planarArea:F3} turned over {where}.");
        }
    }

    private static SurfaceFrame FrameAt(Vec3 origin, Vec3 normal, double degrees)
    {
        var u = Vec3.UnitX - (normal * normal.Dot(Vec3.UnitX));
        u /= u.Length;
        var w = normal.Cross(u);
        var radians = degrees * Math.PI / 180;
        u = (u * Math.Cos(radians)) + (w * Math.Sin(radians));
        return new SurfaceFrame(origin, u, normal.Cross(u), normal);
    }

    /// <summary>
    /// About the size and make-up of a 10mm bold word: a round letter, a letter with two
    /// counters, a pointed one with a counter, and a plain stroke, side by side.
    /// </summary>
    private static ImmutableArray<PlanarPolygon> GlyphLikeOutlines()
    {
        static ImmutableArray<Vec2> Circle(double cx, double cy, double r, bool clockwise)
        {
            var ring = Enumerable.Range(0, 48)
                .Select(i => new Vec2(cx + (r * Math.Cos(i * Math.PI / 24)), cy + (r * Math.Sin(i * Math.PI / 24))));
            return clockwise ? [.. ring.Reverse()] : [.. ring];
        }

        return
        [
            new PlanarPolygon(Circle(-12, 0, 5, false), [Circle(-12, 0, 2.6, true)]),
            new PlanarPolygon(Assets.Square(-6, -5, 7.5), [Assets.Square(-3.5, -3, 3, true), Assets.Square(-3.5, 1, 3, true)]),
            new PlanarPolygon(
                [new Vec2(3, -5), new Vec2(6, -5), new Vec2(7, -2), new Vec2(9.5, -2), new Vec2(10.5, -5), new Vec2(13.5, -5), new Vec2(10, 5), new Vec2(6.5, 5)],
                [[new Vec2(7.8, 0.2), new Vec2(8.25, 2), new Vec2(8.7, 0.2)]]),
            PlanarPolygon.FromOuter([new Vec2(15, -5), new Vec2(17.5, -5), new Vec2(17.5, 5), new Vec2(15, 5)]),
        ];
    }

    [Fact]
    public void Outlines_too_small_to_triangulate_are_refused()
    {
        var spec = new DecalPrismSpec([PlanarPolygon.FromOuter([Vec2.Zero, Vec2.Zero.LerpTo(new Vec2(1, 0), 1)])], Flat, 1, 0, 0);

        Check.Equal("Decals.EmptyOutlines", Fixtures.Engine.Decals.BuildPrism(spec).Error.Code);
    }
}
