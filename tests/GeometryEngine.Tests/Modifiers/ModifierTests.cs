using GeometryEngine.Internal.Native;
using GeometryEngine.Internal.Spatial;

namespace GeometryEngine.Tests.Modifiers;

[Suite("Modifiers / offset")]
public sealed class OffsetTests
{
    [Fact]
    public void Offsetting_a_sphere_outwards_grows_it_by_the_distance()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 64);

        var grown = Fixtures.Engine.Modifiers.Offset(sphere, 2, cellSize: 0.5);

        Check.True(grown.IsSuccess);
        Check.RelativelyClose(Fixtures.SphereVolume(12), Fixtures.VolumeOf(grown.Value), 0.02);
        Check.True(Fixtures.TopologyOf(grown.Value).IsWatertight);

        var stats = Fixtures.Engine.Evaluators.GetStatistics(grown.Value).Value;
        Check.Close(12, stats.BoundsMax.Z, 0.25);
    }

    [Fact]
    public void A_negative_offset_shrinks_the_solid()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 64);

        var shrunk = Fixtures.Engine.Modifiers.Offset(sphere, -2, cellSize: 0.5).Value;

        Check.RelativelyClose(Fixtures.SphereVolume(8), Fixtures.VolumeOf(shrunk), 0.03);
    }

    [Fact]
    public void An_offset_records_which_distance_field_produced_it()
    {
        var grown = Fixtures.Engine.Modifiers.Offset(Fixtures.UnitCube(), 0.2).Value;

        var expected = DistanceFieldNative.IsAvailable
            ? "GeometryEngine.Modifiers.Offset (native field)"
            : "GeometryEngine.Modifiers.Offset (managed field)";
        Check.Equal(expected, grown.Metadata.CreatedBy);
    }

    [Fact]
    public void The_native_and_managed_fields_offset_to_the_same_solid()
    {
        // The managed field is what runs wherever the native library does not ship, so it has to
        // give the same answer, not merely an answer.
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 48);
        var native = Fixtures.Engine.Modifiers.Offset(sphere, 1.5, cellSize: 1).Value;

        var bvh = new MeshBvh(sphere);
        var padding = new Vec3(1, 1, 1) * 3.5;
        var managed = ManifoldKernel.LevelSet(
            bvh.SignedDistance, new Vec3(-10, -10, -10) - padding, new Vec3(10, 10, 10) + padding, 1, 1.5, sphere.Metadata).Value;

        Check.RelativelyClose(Fixtures.VolumeOf(native), Fixtures.VolumeOf(managed), 0.01);
    }

    [Fact]
    public void An_erode_dilate_across_a_concave_crease_keeps_the_solid_whole()
    {
        var peanut = Fixtures.Engine.Booleans.Union(
            Fixtures.Sphere(new Vec3(-7, 0, 0), 12, 32),
            Fixtures.Sphere(new Vec3(7, 0, 0), 12, 32)).Value;
        var before = Fixtures.VolumeOf(peanut);

        var closed = Fixtures.Engine.Modifiers.DoubleOffset(peanut, 1, iterations: 1, cellSize: 1);

        Check.True(closed.IsSuccess);
        Check.Equal(1, Fixtures.Engine.Evaluators.SeparateComponents(closed.Value).Value.Length);
        Check.RelativelyClose(before, Fixtures.VolumeOf(closed.Value), 0.1);
    }

    [Fact]
    public void Smoothing_a_real_bolus_keeps_it_one_piece_at_its_original_size()
    {
        // A sphere hides a broken distance field; a scanned bolus does not. A per-face sign once
        // shredded this very file into 25 pieces in a box half again too big.
        foreach (var name in new[] { "ear_bolus.stl", "eye_bolus.stl" })
        {
            var bolus = Assets.LoadBench(name);
            var before = Fixtures.Engine.Evaluators.GetStatistics(bolus).Value;

            var smoothed = Fixtures.Engine.Modifiers.DoubleOffset(bolus, 1, iterations: 1, cellSize: 1).Value;
            var after = Fixtures.Engine.Evaluators.GetStatistics(smoothed).Value;

            Check.Equal(1, Fixtures.Engine.Evaluators.SeparateComponents(smoothed).Value.Length);
            Check.True(Fixtures.TopologyOf(smoothed).IsWatertight);
            Check.Less((after.BoundsMin - before.BoundsMin).Length, 2.0);
            Check.Less((after.BoundsMax - before.BoundsMax).Length, 2.0);
            Check.RelativelyClose(before.Volume, after.Volume, 0.2);
        }
    }

    [Fact]
    public void A_fine_cell_on_a_large_model_is_coarsened_rather_than_run_away()
    {
        // One-tenth-unit cells over a 160-unit box would be billions of samples.
        var large = Fixtures.Sphere(Vec3.Zero, 80, 48);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var grown = Fixtures.Engine.Modifiers.Offset(large, 1, cellSize: 0.1);

        Check.True(grown.IsSuccess);
        Check.Less(started.Elapsed.TotalSeconds, 30);
    }

    [Fact]
    public void An_empty_mesh_cannot_be_offset()
    {
        Check.Equal("Mesh.EmptyOperand", Fixtures.Engine.Modifiers.Offset(ImmutableMesh.Empty, 1).Error.Code);
    }
}

[Suite("Modifiers / decimate, repair")]
public sealed class DecimateAndRepairTests
{
    [Fact]
    public void Decimating_a_sphere_reaches_the_target_and_stays_a_clean_solid()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 64);

        var reduced = Fixtures.Engine.Modifiers.Decimate(sphere, 600).Value;
        var topology = Fixtures.TopologyOf(reduced);

        Check.LessOrEqual(reduced.TriangleCount, 600);
        Check.Greater(reduced.TriangleCount, 400);
        Check.True(topology.IsWatertight);
        Check.True(topology.IsConsistentlyWound);
        Check.Equal(0, Fixtures.Engine.Evaluators.CountSelfIntersections(reduced).Value);
        Check.RelativelyClose(Fixtures.VolumeOf(sphere), Fixtures.VolumeOf(reduced), 0.05);
    }

    [Fact]
    public void Decimating_a_real_bolus_keeps_it_closed_and_its_shape()
    {
        var bolus = Assets.LoadBench("eye_bolus.stl");
        var target = bolus.TriangleCount / 4;

        var reduced = Fixtures.Engine.Modifiers.Decimate(bolus, target).Value;

        Check.LessOrEqual(reduced.TriangleCount, target + 2);
        Check.True(Fixtures.TopologyOf(reduced).IsWatertight);
        Check.RelativelyClose(Fixtures.VolumeOf(bolus), Fixtures.VolumeOf(reduced), 0.02);
    }

    [Fact]
    public void A_mesh_already_below_the_target_comes_back_unreduced()
    {
        var cube = Fixtures.UnitCube();

        Check.Equal(12, Fixtures.Engine.Modifiers.Decimate(cube, 100).Value.TriangleCount);
    }

    [Fact]
    public void Repair_welds_a_triangle_soup_back_into_a_solid()
    {
        var soup = Assets.Unwelded(Fixtures.UnitCube());
        Check.Equal(36, soup.VertexCount);

        var repaired = Fixtures.Engine.Modifiers.Repair(soup).Value;

        Check.Equal(8, repaired.VertexCount);
        Check.Equal(12, repaired.TriangleCount);
        Check.True(Fixtures.TopologyOf(repaired).IsClean);
    }

    [Fact]
    public void Repair_drops_a_face_doubled_back_on_itself_and_a_repeated_face()
    {
        var cube = Fixtures.UnitCube();
        var t = cube.Triangles;

        // Append the first face wound backwards, and the second face repeated as-is.
        var damaged = Fixtures.Engine.CreateMesh(
            cube.Vertices,
            t.AddRange(ImmutableArray.Create(t[0], t[2], t[1], t[3], t[4], t[5])),
            MeshMetadata.Named("damaged")).Value;
        Check.False(Fixtures.TopologyOf(damaged).IsEdgeManifold);

        var repaired = Fixtures.Engine.Modifiers.Repair(damaged).Value;
        var topology = Fixtures.TopologyOf(repaired);

        // The inverted pair cancels entirely, which opens the one face it replaced.
        Check.Equal(11, repaired.TriangleCount);
        Check.Equal(0, topology.DuplicateFaceCount);
        Check.True(topology.IsEdgeManifold);
    }

    [Fact]
    public void Repair_drops_vertices_nothing_uses()
    {
        var cube = Fixtures.UnitCube();
        var padded = Fixtures.Engine.CreateMesh(cube.Vertices.Add(new Vec3(9, 9, 9)), cube.Triangles, MeshMetadata.Named("padded")).Value;
        Check.Equal(1, Fixtures.TopologyOf(padded).UnreferencedVertexCount);

        Check.Equal(8, Fixtures.Engine.Modifiers.Repair(padded).Value.VertexCount);
    }

    [Fact]
    public void Repairing_self_intersections_recuts_overlapping_solids_into_their_union()
    {
        var overlapping = Assets.Concatenate(Fixtures.Cube(0, 2), Fixtures.Cube(1, 2));
        Check.Greater(Fixtures.Engine.Evaluators.CountSelfIntersections(overlapping).Value, 0);

        var repaired = Fixtures.Engine.Modifiers.RepairSelfIntersections(overlapping).Value;

        Check.Equal(0, Fixtures.Engine.Evaluators.CountSelfIntersections(repaired).Value);
        Check.Close(15, Fixtures.VolumeOf(repaired), 1e-9);
        Check.True(Fixtures.TopologyOf(repaired).IsWatertight);
    }
}
