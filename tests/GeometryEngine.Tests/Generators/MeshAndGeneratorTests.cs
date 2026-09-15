namespace GeometryEngine.Tests.Generators;

[Suite("Mesh / construction invariants")]
public sealed class MeshConstructionTests
{
    private static ImmutableArray<Vec3> ThreeVertices() =>
        ImmutableArray.Create(Vec3.Zero, Vec3.UnitX, Vec3.UnitY);

    [Fact]
    public void A_valid_triangle_builds_a_mesh()
    {
        var mesh = ImmutableMesh.Create(ThreeVertices(), ImmutableArray.Create(0, 1, 2), MeshMetadata.Named("t"));

        Check.True(mesh.IsSuccess);
        Check.Equal(1, mesh.Value.TriangleCount);
        Check.Equal(3, mesh.Value.VertexCount);
    }

    [Fact]
    public void An_index_count_that_is_not_a_multiple_of_three_is_refused()
    {
        var mesh = ImmutableMesh.Create(ThreeVertices(), ImmutableArray.Create(0, 1), MeshMetadata.Anonymous);

        Check.True(mesh.IsFailure);
        Check.Equal("Mesh.RaggedTriangleArray", mesh.Error.Code);
    }

    [Fact]
    public void An_index_that_addresses_no_vertex_is_refused()
    {
        var mesh = ImmutableMesh.Create(ThreeVertices(), ImmutableArray.Create(0, 1, 9), MeshMetadata.Anonymous);

        Check.True(mesh.IsFailure);
        Check.Equal("Mesh.IndexOutOfRange", mesh.Error.Code);
    }

    [Fact]
    public void A_non_finite_vertex_is_refused()
    {
        var vertices = ImmutableArray.Create(Vec3.Zero, Vec3.UnitX, new Vec3(0, double.PositiveInfinity, 0));
        var mesh = ImmutableMesh.Create(vertices, ImmutableArray.Create(0, 1, 2), MeshMetadata.Anonymous);

        Check.True(mesh.IsFailure);
        Check.Equal("Mesh.NonFiniteVertex", mesh.Error.Code);
    }

    [Fact]
    public void Changing_metadata_leaves_the_original_mesh_untouched()
    {
        var original = ImmutableMesh.Create(ThreeVertices(), ImmutableArray.Create(0, 1, 2), MeshMetadata.Named("first")).Value;
        var renamed = original.WithMetadata(MeshMetadata.Named("second"));

        Check.Equal("first", original.Metadata.Name);
        Check.Equal("second", renamed.Metadata.Name);
        Check.False(ReferenceEquals(original, renamed));
    }
}

[Suite("Generators")]
public sealed class GeneratorTests
{
    [Fact]
    public void A_box_is_twelve_triangles()
    {
        Check.Equal(12, Fixtures.UnitCube().TriangleCount);
    }

    [Fact]
    public void A_box_measures_its_analytic_volume()
    {
        var box = Fixtures.Box(new Vec3(-1, -2, -3), new Vec3(2, 2, 0));

        Check.RelativelyClose(3 * 4 * 3, Fixtures.VolumeOf(box), 1e-12);
    }

    [Fact]
    public void A_box_measures_its_analytic_surface_area()
    {
        var statistics = Fixtures.Engine.Evaluators.GetStatistics(Fixtures.UnitCube()).Value;

        Check.RelativelyClose(6, statistics.SurfaceArea, 1e-12);
    }

    [Fact]
    public void A_box_is_watertight_and_clean()
    {
        Check.True(Fixtures.TopologyOf(Fixtures.UnitCube()).IsClean);
    }

    [Fact]
    public void A_box_with_an_inverted_span_is_refused()
    {
        var box = Fixtures.Engine.Generators.GenerateBox(new Vec3(1, 1, 1), new Vec3(0, 2, 2));

        Check.True(box.IsFailure);
        Check.Equal("Generators.DegenerateBox", box.Error.Code);
    }

    [Fact]
    public void A_sphere_approaches_its_analytic_volume_as_it_is_refined()
    {
        var coarse = Fixtures.VolumeOf(Fixtures.Sphere(Vec3.Zero, 1, 12));
        var fine = Fixtures.VolumeOf(Fixtures.Sphere(Vec3.Zero, 1, 96));
        var exact = Fixtures.SphereVolume(1);

        Check.Less(Math.Abs(exact - fine), Math.Abs(exact - coarse));
        Check.RelativelyClose(exact, fine, 0.002);
    }

    [Fact]
    public void A_sphere_is_watertight()
    {
        Check.True(Fixtures.TopologyOf(Fixtures.Sphere(Vec3.Zero, 2, 24)).IsWatertight);
    }

    [Fact]
    public void A_sphere_with_a_non_positive_radius_is_refused()
    {
        var sphere = Fixtures.Engine.Generators.GenerateSphere(Vec3.Zero, 0);

        Check.True(sphere.IsFailure);
        Check.Equal("Generators.NonPositiveRadius", sphere.Error.Code);
    }

    [Fact]
    public void A_cylinder_approaches_its_analytic_volume()
    {
        var cylinder = Fixtures.Cylinder(Vec3.Zero, 1, 2, 256);

        Check.RelativelyClose(Math.PI * 1 * 1 * 2, Fixtures.VolumeOf(cylinder), 0.001);
    }

    [Fact]
    public void A_cylinder_is_watertight()
    {
        Check.True(Fixtures.TopologyOf(Fixtures.Cylinder(Vec3.Zero, 1, 2, 32)).IsWatertight);
    }

    [Fact]
    public void A_cylinder_with_too_few_segments_is_refused()
    {
        var cylinder = Fixtures.Engine.Generators.GenerateCylinder(Vec3.Zero, 1, 1, 2);

        Check.True(cylinder.IsFailure);
        Check.Equal("Generators.TooFewSegments", cylinder.Error.Code);
    }
}
