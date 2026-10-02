namespace GeometryEngine.Tests.Evaluators;

/// <summary>
/// A mesh remembers what the engine measured of it. A cache hit is visible as the very same
/// result instance coming back - a fresh measurement builds a new one.
/// </summary>
[Suite("Evaluators / measurement cache")]
public sealed class MeasurementCacheTests
{
    private static IGeometryEvaluators Evaluators => Fixtures.Engine.Evaluators;

    [Fact]
    public void Measuring_a_mesh_twice_reads_the_first_answer()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 24);

        Check.True(ReferenceEquals(Evaluators.GetStatistics(sphere).Value, Evaluators.GetStatistics(sphere).Value));
        Check.True(ReferenceEquals(Evaluators.ValidateTopology(sphere).Value, Evaluators.ValidateTopology(sphere).Value));
        Check.True(Evaluators.ComputeVertexNormals(sphere).Value == Evaluators.ComputeVertexNormals(sphere).Value);
    }

    [Fact]
    public void A_copy_with_new_metadata_shares_what_was_measured()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 24);
        var statistics = Evaluators.GetStatistics(sphere).Value;
        var topology = Evaluators.ValidateTopology(sphere).Value;

        var renamed = sphere.WithMetadata(sphere.Metadata.WithName("renamed"));

        Check.True(ReferenceEquals(statistics, Evaluators.GetStatistics(renamed).Value));
        Check.True(ReferenceEquals(topology, Evaluators.ValidateTopology(renamed).Value));
    }

    [Fact]
    public void A_translation_carries_the_measurements_with_the_bounds_moved()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 24);
        var topology = Evaluators.ValidateTopology(sphere).Value;
        var before = Evaluators.GetStatistics(sphere).Value;
        var normals = Evaluators.ComputeVertexNormals(sphere).Value;

        var moved = Fixtures.Engine.Transforms.Translate(sphere, new Vec3(10, -4, 2)).Value;
        var after = Evaluators.GetStatistics(moved).Value;
        var fresh = Evaluators.GetStatistics(Unmeasured(moved)).Value;

        Check.True(ReferenceEquals(topology, Evaluators.ValidateTopology(moved).Value));
        Check.Equal(fresh.BoundsMin, after.BoundsMin);
        Check.Equal(fresh.BoundsMax, after.BoundsMax);
        Check.Equal(before.Volume, after.Volume);
        Check.RelativelyClose(fresh.Volume, after.Volume, 1e-9);
        Check.True(normals.SequenceEqual(Evaluators.ComputeVertexNormals(moved).Value));
    }

    [Fact]
    public void A_rotation_carries_the_topology_and_turns_the_normals()
    {
        var box = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(4, 2, 1));
        var topology = Evaluators.ValidateTopology(box).Value;
        Evaluators.GetStatistics(box);
        Evaluators.ComputeVertexNormals(box);

        var turned = Fixtures.Engine.Transforms.Rotate(box, Direction.From(new Vec3(1, 2, 3)).Value, 0.7).Value;
        var fresh = Unmeasured(turned);

        Check.True(ReferenceEquals(topology, Evaluators.ValidateTopology(turned).Value));

        var carried = Evaluators.GetStatistics(turned).Value;
        var measured = Evaluators.GetStatistics(fresh).Value;
        Check.Equal(measured.BoundsMin, carried.BoundsMin);
        Check.Equal(measured.BoundsMax, carried.BoundsMax);
        Check.RelativelyClose(measured.Volume, carried.Volume, 1e-9);
        Check.RelativelyClose(measured.SurfaceArea, carried.SurfaceArea, 1e-9);

        var carriedNormals = Evaluators.ComputeVertexNormals(turned).Value;
        var measuredNormals = Evaluators.ComputeVertexNormals(fresh).Value;
        for (var i = 0; i < carriedNormals.Length; i++)
        {
            Check.Less(carriedNormals[i].DistanceTo(measuredNormals[i]), 1e-9);
        }
    }

    [Fact]
    public void A_scale_measures_afresh()
    {
        var cube = Fixtures.UnitCube();
        var statistics = Evaluators.GetStatistics(cube).Value;

        var scaled = Fixtures.Engine.Transforms.Scale(cube, new Vec3(2, 3, 4)).Value;

        Check.False(ReferenceEquals(statistics, Evaluators.GetStatistics(scaled).Value));
        Check.RelativelyClose(statistics.Volume * 24, Evaluators.GetStatistics(scaled).Value.Volume, 1e-9);
    }

    [Fact]
    public void An_engine_with_another_tolerance_takes_its_own_topology_audit()
    {
        // Two corners a micron apart: coincident at a millimetre's tolerance, distinct at the default.
        var vertices = ImmutableArray.Create(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, new Vec3(1e-6, 0, 0));
        var mesh = ImmutableMesh.Create(vertices, ImmutableArray.Create(0, 1, 2), MeshMetadata.Named("near")).Value;
        var coarse = BspGeometryEngine.Create(Tolerance.From(1e-3).Value);

        Check.Equal(0, Evaluators.ValidateTopology(mesh).Value.DuplicateVertexCount);
        Check.Equal(1, coarse.Evaluators.ValidateTopology(mesh).Value.DuplicateVertexCount);
        Check.Equal(0, Evaluators.ValidateTopology(mesh).Value.DuplicateVertexCount);
    }

    /// <summary>The same geometry as a mesh nothing has measured yet.</summary>
    private static IMesh Unmeasured(IMesh mesh) =>
        ImmutableMesh.Create(mesh.Vertices, mesh.Triangles, MeshMetadata.Named("fresh")).Value;
}
