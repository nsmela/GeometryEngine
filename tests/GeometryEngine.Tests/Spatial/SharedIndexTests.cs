using GeometryEngine.Spatial;

namespace GeometryEngine.Tests.Spatial;

// The spheres here are coarse, and their facets sit up to a fifth of a unit inside the radius,
// so a distance read from the centre is checked to within that.
[Suite("Spatial / the index kept with a mesh")]
public sealed class SharedIndexTests
{
    private static ISpatialQueries Spatial => Fixtures.Engine.Spatial;

    [Fact]
    public void Asking_twice_gives_the_same_index()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 16);

        Check.True(ReferenceEquals(Spatial.IndexFor(sphere).Value, Spatial.IndexFor(sphere).Value));
    }

    [Fact]
    public void A_copy_with_new_metadata_shares_it()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 16);
        var renamed = sphere.WithMetadata(MeshMetadata.Named("renamed"));

        Check.True(ReferenceEquals(Spatial.IndexFor(sphere).Value, Spatial.IndexFor(renamed).Value));
    }

    [Fact]
    public void Disposing_it_leaves_it_working_for_everyone_else()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 16);

        using (var index = Spatial.IndexFor(sphere).Value)
        {
            _ = index.SignedDistance(Vec3.Zero);
        }

        Check.Close(-5, Spatial.IndexFor(sphere).Value.SignedDistance(Vec3.Zero), 0.25);
    }

    [Fact]
    public void A_moved_mesh_has_its_own_index_over_its_new_position()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 16);
        _ = Spatial.IndexFor(sphere).Value;

        var moved = Fixtures.Engine.Transforms.Translate(sphere, new Vec3(100, 0, 0)).Value;
        var index = Spatial.IndexFor(moved).Value;

        Check.False(ReferenceEquals(Spatial.IndexFor(sphere).Value, index));
        Check.Close(-5, index.SignedDistance(new Vec3(100, 0, 0)), 0.25);
    }

    [Fact]
    public void Measuring_deviation_against_a_reference_leaves_its_index_built()
    {
        var reference = Fixtures.Sphere(Vec3.Zero, 5, 16);
        var built = (ImmutableMesh)reference;

        _ = Fixtures.Engine.Evaluators.MeasureDeviation(Fixtures.Sphere(Vec3.Zero, 5.5, 16), reference).Value;

        var kept = built.Measurements.Index(() => throw new InvalidOperationException("the index was not kept"));
        Check.True(ReferenceEquals(kept, Spatial.IndexFor(reference).Value));
    }

    [Fact]
    public void A_prism_laid_on_a_surface_uses_the_surfaces_own_index()
    {
        var surface = Fixtures.Sphere(Vec3.Zero, 20, 32);
        var spec = new DecalPrismSpec(
            [PlanarPolygon.FromOuter(Assets.Square(-2, -2, 4))],
            SurfaceFrame.FromNormal(new Vec3(0, 0, 20), Vec3.UnitZ),
            Depth: 1, Sink: -0.2, Overshoot: 0.2, MaxEdgeLength: 0.5,
            Surface: Maybe<IMesh>.Some(surface));

        _ = Fixtures.Engine.Decals.BuildPrism(spec).Value;

        var kept = ((ImmutableMesh)surface).Measurements.Index(() => throw new InvalidOperationException("the index was not kept"));
        Check.True(kept is SharedSpatialIndex);
    }

    [Fact]
    public void An_empty_mesh_has_no_index()
    {
        Check.True(Spatial.IndexFor(ImmutableMesh.Empty).IsFailure);
    }
}
