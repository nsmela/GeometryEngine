namespace GeometryEngine.Tests.Evaluators;

[Suite("Evaluators")]
public sealed class EvaluatorTests
{
    [Fact]
    public void Statistics_report_the_bounding_box()
    {
        var statistics = Fixtures.Engine.Evaluators.GetStatistics(Fixtures.Box(new Vec3(-1, 0, 2), new Vec3(3, 1, 5))).Value;

        Check.Equal(new Vec3(-1, 0, 2), statistics.BoundsMin);
        Check.Equal(new Vec3(3, 1, 5), statistics.BoundsMax);
        Check.Equal(new Vec3(4, 1, 3), statistics.BoundsSize);
    }

    [Fact]
    public void An_open_mesh_reports_its_boundary_edges()
    {
        var vertices = ImmutableArray.Create(Vec3.Zero, Vec3.UnitX, Vec3.UnitY);
        var triangle = ImmutableMesh.Create(vertices, ImmutableArray.Create(0, 1, 2), MeshMetadata.Named("open")).Value;

        var topology = Fixtures.Engine.Evaluators.ValidateTopology(triangle).Value;

        Check.Equal(3, topology.BoundaryEdgeCount);
        Check.False(topology.IsWatertight);
    }

    [Fact]
    public void A_duplicated_vertex_is_reported()
    {
        var vertices = ImmutableArray.Create(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.Zero);
        var mesh = ImmutableMesh.Create(vertices, ImmutableArray.Create(0, 1, 2), MeshMetadata.Named("dup")).Value;

        Check.Equal(1, Fixtures.Engine.Evaluators.ValidateTopology(mesh).Value.DuplicateVertexCount);
    }

    [Fact]
    public void Two_disjoint_boxes_separate_into_two_components()
    {
        var pair = Fixtures.Engine.Booleans.Union(Fixtures.Cube(0, 1), Fixtures.Cube(5, 1)).Value;

        var components = Fixtures.Engine.Evaluators.SeparateComponents(pair).Value;

        Check.Equal(2, components.Length);
        Check.RelativelyClose(1, Fixtures.VolumeOf(components[0]), 1e-9);
        Check.RelativelyClose(1, Fixtures.VolumeOf(components[1]), 1e-9);
    }

    [Fact]
    public void A_single_solid_separates_into_one_component()
    {
        Check.Equal(1, Fixtures.Engine.Evaluators.SeparateComponents(Fixtures.UnitCube()).Value.Length);
    }

    [Fact]
    public void An_empty_mesh_cannot_be_measured()
    {
        var statistics = Fixtures.Engine.Evaluators.GetStatistics(ImmutableMesh.Empty);

        Check.True(statistics.IsFailure);
        Check.Equal("Mesh.EmptyOperand", statistics.Error.Code);
    }
}

[Suite("Transforms")]
public sealed class TransformTests
{
    [Fact]
    public void Translation_moves_the_bounds_and_keeps_the_volume()
    {
        var moved = Fixtures.Engine.Transforms.Translate(Fixtures.UnitCube(), new Vec3(10, 0, -4)).Value;
        var statistics = Fixtures.Engine.Evaluators.GetStatistics(moved).Value;

        Check.Equal(new Vec3(10, 0, -4), statistics.BoundsMin);
        Check.RelativelyClose(1, statistics.Volume, 1e-12);
    }

    [Fact]
    public void Scaling_multiplies_the_volume_by_the_product_of_the_factors()
    {
        var scaled = Fixtures.Engine.Transforms.Scale(Fixtures.UnitCube(), new Vec3(2, 3, 4)).Value;

        Check.RelativelyClose(24, Fixtures.VolumeOf(scaled), 1e-12);
    }

    [Fact]
    public void Rotation_preserves_volume_and_surface_area()
    {
        var cube = Fixtures.UnitCube();
        var axis = Direction.From(new Vec3(1, 1, 1)).Value;
        var rotated = Fixtures.Engine.Transforms.Rotate(cube, axis, Math.PI / 5).Value;

        var before = Fixtures.Engine.Evaluators.GetStatistics(cube).Value;
        var after = Fixtures.Engine.Evaluators.GetStatistics(rotated).Value;

        Check.RelativelyClose(before.Volume, after.Volume, 1e-12);
        Check.RelativelyClose(before.SurfaceArea, after.SurfaceArea, 1e-12);
    }

    [Fact]
    public void A_full_turn_returns_the_original_positions()
    {
        var cube = Fixtures.UnitCube();
        var turned = Fixtures.Engine.Transforms.Rotate(cube, Direction.Z, 2 * Math.PI).Value;

        for (var i = 0; i < cube.VertexCount; i++)
        {
            Check.Close(0, cube.Vertices[i].DistanceTo(turned.Vertices[i]), 1e-12);
        }
    }

    [Fact]
    public void A_transform_never_touches_its_input()
    {
        var cube = Fixtures.UnitCube();
        var before = cube.Vertices;

        _ = Fixtures.Engine.Transforms.Translate(cube, new Vec3(3, 3, 3)).Value;

        Check.True(before.SequenceEqual(cube.Vertices));
    }

    [Fact]
    public void A_mirroring_scale_is_refused_because_it_would_invert_the_solid()
    {
        var scaled = Fixtures.Engine.Transforms.Scale(Fixtures.UnitCube(), new Vec3(1, -1, 1));

        Check.True(scaled.IsFailure);
        Check.Equal("Transforms.MirroringScale", scaled.Error.Code);
    }

    [Fact]
    public void Alignment_recovers_a_translated_and_rotated_mesh()
    {
        // A slab with a block on one corner: no rotation maps it onto itself, so there is one
        // right answer to recover, and its handful of vertices leaves the sampling to the faces.
        var target = Fixtures.Engine.Booleans.Union(
            Fixtures.Box(new Vec3(-2, -1, -0.5), new Vec3(2, 1, 0.5)),
            Fixtures.Box(new Vec3(1.2, 0.4, 0.5), new Vec3(2, 1, 1.3))).Value;
        var axis = Direction.From(new Vec3(1, 2, 3)).Value;
        var rotated = Fixtures.Engine.Transforms.Rotate(target, axis, 0.25).Value;
        var displaced = Fixtures.Engine.Transforms.Translate(rotated, new Vec3(12, -7, 4)).Value;

        // The defaults: a machined shape like this should settle well inside them.
        var aligned = Fixtures.Engine.Transforms.Align(displaced, target);

        Check.True(aligned.IsSuccess);
        var result = aligned.Value;
        Check.Less(result.RootMeanSquareError, 1e-6);
        Check.Less(result.Iterations, 20);

        // The reported motion is the one that undoes the displacement, and the one that was
        // applied to make the aligned mesh.
        for (var i = 0; i < target.VertexCount; i++)
        {
            var moved = result.Rotation.Apply(displaced.Vertices[i]) + result.Translation;
            Check.Less(moved.DistanceTo(target.Vertices[i]), 1e-6);
            Check.Less(moved.DistanceTo(result.AlignedMesh.Vertices[i]), 1e-9);
        }
    }

    [Fact]
    public void Alignment_centres_a_differently_tessellated_copy_without_spinning_it()
    {
        // A sphere pins no rotation, and the two meshes share no vertices: the start must not
        // lean towards the denser one, and the free spin must stay put rather than wander.
        var target = Fixtures.Sphere(Vec3.Zero, 3, 16);
        var source = Fixtures.Sphere(new Vec3(5, -2, 1), 3, 48);

        var aligned = Fixtures.Engine.Transforms.Align(source, target);

        Check.True(aligned.IsSuccess);
        var result = aligned.Value;
        var centre = result.Rotation.Apply(new Vec3(5, -2, 1)) + result.Translation;
        Check.Less(centre.Length, 0.02);
        Check.Greater(result.Rotation.W, Math.Cos(0.01));

        // What is left is the two tessellations disagreeing: a 16-segment facet sits up to
        // 3 (1 - cos(pi/16)) = 0.058 inside the round surface the finer copy follows.
        Check.Less(result.RootMeanSquareError, 0.08);
    }

    [Fact]
    public void Alignment_refuses_fewer_than_three_sample_points()
    {
        var aligned = Fixtures.Engine.Transforms.Align(Fixtures.UnitCube(), Fixtures.UnitCube(), maxSamplePoints: 2);

        Check.True(aligned.IsFailure);
        Check.Equal("Transforms.InvalidAlignmentParameters", aligned.Error.Code);
    }

    [Fact]
    public void Alignment_refuses_an_empty_mesh()
    {
        var aligned = Fixtures.Engine.Transforms.Align(ImmutableMesh.Empty, Fixtures.UnitCube());

        Check.True(aligned.IsFailure);
        Check.Equal("Mesh.EmptyOperand", aligned.Error.Code);
    }
}
