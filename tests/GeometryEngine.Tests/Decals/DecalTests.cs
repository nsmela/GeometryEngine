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
    public void Outlines_too_small_to_triangulate_are_refused()
    {
        var spec = new DecalPrismSpec([PlanarPolygon.FromOuter([Vec2.Zero, Vec2.Zero.LerpTo(new Vec2(1, 0), 1)])], Flat, 1, 0, 0);

        Check.Equal("Decals.EmptyOutlines", Fixtures.Engine.Decals.BuildPrism(spec).Error.Code);
    }
}
