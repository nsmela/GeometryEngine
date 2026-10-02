using GeometryEngine.Booleans;

namespace GeometryEngine.Tests.Booleans;

/// <summary>
/// Cutting a mesh along a plane, and combining many meshes in one operation. Each is checked on
/// the native kernel and on the managed one, which is also what the native kernel falls back to.
/// </summary>
[Suite("Booleans / split and batch")]
public sealed class SplitAndBatchTests
{
    private static readonly IGeometryEngine Managed = BspGeometryEngine.CreateManagedBsp();

    private static IBooleans Native => Fixtures.Engine.Booleans;

    private static Plane Horizontal(double z, bool up = true) =>
        Plane.FromNormalAndPoint(up ? Direction.Z : Direction.Z.Flipped(), new Vec3(0, 0, z));

    [Fact]
    public void Splitting_a_box_across_its_middle_gives_two_closed_halves()
    {
        var split = Native.Split(Fixtures.Cube(0, 2), Horizontal(0.5)).Value;

        Check.RelativelyClose(6, Fixtures.VolumeOf(split.Front), 1e-9);
        Check.RelativelyClose(2, Fixtures.VolumeOf(split.Back), 1e-9);
        Check.True(Fixtures.TopologyOf(split.Front).IsWatertight);
        Check.True(Fixtures.TopologyOf(split.Back).IsWatertight);
        Check.Equal(ManifoldBooleanOperations.NativeProducer, split.Front.Metadata.CreatedBy);
    }

    [Fact]
    public void The_front_half_is_the_side_the_normal_points_to()
    {
        var up = Native.Split(Fixtures.Cube(0, 2), Horizontal(0.5)).Value;
        var down = Native.Split(Fixtures.Cube(0, 2), Horizontal(0.5, up: false)).Value;

        Check.Close(0.5, Fixtures.Engine.Evaluators.GetStatistics(up.Front).Value.BoundsMin.Z, 1e-9);
        Check.Close(0.5, Fixtures.Engine.Evaluators.GetStatistics(down.Front).Value.BoundsMax.Z, 1e-9);
    }

    [Fact]
    public void A_plane_that_misses_the_mesh_leaves_it_whole_on_one_side()
    {
        var split = Native.Split(Fixtures.UnitCube(), Horizontal(-5)).Value;

        Check.RelativelyClose(1, Fixtures.VolumeOf(split.Front), 1e-9);
        Check.True(split.Back.IsEmpty);
    }

    [Fact]
    public void A_tilted_cut_through_a_sphere_keeps_all_of_its_volume()
    {
        var sphere = Fixtures.Sphere(new Vec3(3, -2, 7), 4);
        var plane = Plane.FromNormalAndPoint(Direction.From(new Vec3(1, 2, -0.5)).Value, new Vec3(4, -1, 7));

        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var split = booleans.Split(sphere, plane).Value;

            Check.RelativelyClose(Fixtures.VolumeOf(sphere), Fixtures.VolumeOf(split.Front) + Fixtures.VolumeOf(split.Back), 1e-6);
            Check.True(Fixtures.TopologyOf(split.Front).IsClosed);
            Check.True(Fixtures.TopologyOf(split.Back).IsClosed);
        }
    }

    [Fact]
    public void The_managed_kernel_splits_to_the_same_halves()
    {
        var split = Managed.Booleans.Split(Fixtures.Cube(0, 2), Horizontal(0.5)).Value;

        Check.RelativelyClose(6, Fixtures.VolumeOf(split.Front), 1e-9);
        Check.RelativelyClose(2, Fixtures.VolumeOf(split.Back), 1e-9);
        Check.Close(0.5, Fixtures.Engine.Evaluators.GetStatistics(split.Front).Value.BoundsMin.Z, 1e-9);
    }

    [Fact]
    public void Halves_are_named_after_the_mesh_they_were_cut_from()
    {
        var cube = Fixtures.UnitCube().WithMetadata(MeshMetadata.Named("bolus"));

        var split = Native.Split(cube, Horizontal(0.5)).Value;

        Check.Equal("bolus front", split.Front.Metadata.Name);
        Check.Equal("bolus back", split.Back.Metadata.Name);
    }

    [Fact]
    public void An_empty_mesh_cannot_be_split()
    {
        Check.Equal(MeshErrors.EmptyOperand.Code, Native.Split(ImmutableMesh.Empty, Horizontal(0)).Error.Code);
    }

    [Fact]
    public void A_batch_union_counts_every_overlap_once()
    {
        // Three cubes of side 2 in a row, each overlapping the next by a unit slab: 3 x 8 - 2 x 4.
        ImmutableArray<IMesh> row = [Fixtures.Box(Vec3.Zero, new Vec3(2, 2, 2)), Fixtures.Box(new Vec3(1, 0, 0), new Vec3(3, 2, 2)), Fixtures.Box(new Vec3(2, 0, 0), new Vec3(4, 2, 2))];

        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var union = booleans.Union(row).Value;

            Check.RelativelyClose(16, Fixtures.VolumeOf(union), 1e-9);
            Check.True(Fixtures.TopologyOf(union).IsClosed);
        }
    }

    [Fact]
    public void A_batch_subtraction_takes_every_tool_away()
    {
        // Two overlapping 2 x 2 x 2 tools inside a 10-cube remove 8 + 8 - 4 between them.
        var block = Fixtures.Cube(0, 10);
        ImmutableArray<IMesh> tools = [Fixtures.Box(new Vec3(1, 1, 1), new Vec3(3, 3, 3)), Fixtures.Box(new Vec3(2, 1, 1), new Vec3(4, 3, 3))];

        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var carved = booleans.Subtract(block, tools).Value;

            Check.RelativelyClose(1000 - 12, Fixtures.VolumeOf(carved), 1e-9);
        }
    }

    [Fact]
    public void A_batch_of_one_and_a_subtraction_of_nothing_return_the_mesh_itself()
    {
        var cube = Fixtures.UnitCube();

        Check.True(ReferenceEquals(cube, Native.Union([cube]).Value));
        Check.True(ReferenceEquals(cube, Native.Subtract(cube, []).Value));
    }

    [Fact]
    public void A_batch_needs_operands_and_none_of_them_empty()
    {
        Check.Equal(BooleanErrors.NoOperands.Code, Native.Union([]).Error.Code);
        Check.Equal(MeshErrors.EmptyOperand.Code, Native.Union([Fixtures.UnitCube(), ImmutableMesh.Empty]).Error.Code);
        Check.Equal(MeshErrors.EmptyOperand.Code, Native.Subtract(Fixtures.UnitCube(), [ImmutableMesh.Empty]).Error.Code);
    }

    [Fact]
    public void A_batch_result_is_named_for_its_first_operand_and_the_rest_counted()
    {
        ImmutableArray<IMesh> meshes = [Fixtures.UnitCube().WithMetadata(MeshMetadata.Named("a")), Fixtures.Cube(0.5, 1), Fixtures.Cube(0.7, 1)];

        Check.Equal("a Union 2 meshes", Native.Union(meshes).Value.Metadata.Name);
    }
}
