namespace GeometryEngine.Tests.Booleans;

/// <summary>
/// Overlapping unit-scale boxes whose exact volumes are known by hand.
/// A = [0,2]^3 (volume 8), B = [1,3]^3 (volume 8), overlap = [1,2]^3 (volume 1).
/// </summary>
[Suite("Booleans / boxes with known volumes")]
public sealed class BoxBooleanTests
{
    private static IMesh A() => Fixtures.Cube(0, 2);

    private static IMesh B() => Fixtures.Cube(1, 2);

    [Fact]
    public void Union_of_overlapping_boxes_counts_the_overlap_once()
    {
        var union = Fixtures.Engine.Booleans.Union(A(), B()).Value;

        Check.RelativelyClose(15, Fixtures.VolumeOf(union), 1e-9);
    }

    [Fact]
    public void Intersection_of_overlapping_boxes_is_the_shared_corner()
    {
        var intersection = Fixtures.Engine.Booleans.Intersect(A(), B()).Value;

        Check.RelativelyClose(1, Fixtures.VolumeOf(intersection), 1e-9);
    }

    [Fact]
    public void Intersection_of_overlapping_boxes_occupies_the_shared_bounds()
    {
        var statistics = Fixtures.Engine.Evaluators.GetStatistics(Fixtures.Engine.Booleans.Intersect(A(), B()).Value).Value;

        Check.Close(1, statistics.BoundsMin.X, 1e-9);
        Check.Close(2, statistics.BoundsMax.X, 1e-9);
    }

    [Fact]
    public void Subtraction_removes_exactly_the_overlap()
    {
        var difference = Fixtures.Engine.Booleans.Subtract(A(), B()).Value;

        Check.RelativelyClose(7, Fixtures.VolumeOf(difference), 1e-9);
    }

    [Fact]
    public void Subtraction_is_not_commutative()
    {
        var left = Fixtures.Engine.Booleans.Subtract(Fixtures.Cube(0, 2), Fixtures.Cube(1, 4)).Value;
        var right = Fixtures.Engine.Booleans.Subtract(Fixtures.Cube(1, 4), Fixtures.Cube(0, 2)).Value;

        Check.RelativelyClose(7, Fixtures.VolumeOf(left), 1e-9);
        Check.RelativelyClose(63, Fixtures.VolumeOf(right), 1e-9);
    }

    [Fact]
    public void Union_of_disjoint_boxes_adds_their_volumes()
    {
        var union = Fixtures.Engine.Booleans.Union(Fixtures.Cube(0, 1), Fixtures.Cube(5, 1)).Value;

        Check.RelativelyClose(2, Fixtures.VolumeOf(union), 1e-9);
    }

    [Fact]
    public void Intersection_of_disjoint_boxes_is_empty()
    {
        var intersection = Fixtures.Engine.Booleans.Intersect(Fixtures.Cube(0, 1), Fixtures.Cube(5, 1)).Value;

        Check.True(intersection.IsEmpty);
    }

    [Fact]
    public void Subtracting_a_disjoint_box_leaves_the_original_volume()
    {
        var difference = Fixtures.Engine.Booleans.Subtract(Fixtures.Cube(0, 1), Fixtures.Cube(5, 1)).Value;

        Check.RelativelyClose(1, Fixtures.VolumeOf(difference), 1e-9);
    }

    [Fact]
    public void Subtracting_an_enclosing_box_leaves_nothing()
    {
        var difference = Fixtures.Engine.Booleans.Subtract(Fixtures.Cube(0, 1), Fixtures.Cube(-1, 4)).Value;

        Check.True(difference.IsEmpty);
    }

    [Fact]
    public void Uniting_a_box_with_itself_changes_nothing()
    {
        var union = Fixtures.Engine.Booleans.Union(Fixtures.Cube(0, 2), Fixtures.Cube(0, 2)).Value;

        Check.RelativelyClose(8, Fixtures.VolumeOf(union), 1e-9);
    }

    [Fact]
    public void Intersecting_a_box_with_itself_changes_nothing()
    {
        var intersection = Fixtures.Engine.Booleans.Intersect(Fixtures.Cube(0, 2), Fixtures.Cube(0, 2)).Value;

        Check.RelativelyClose(8, Fixtures.VolumeOf(intersection), 1e-9);
    }

    [Fact]
    public void Subtracting_a_box_from_itself_leaves_nothing()
    {
        var difference = Fixtures.Engine.Booleans.Subtract(Fixtures.Cube(0, 2), Fixtures.Cube(0, 2)).Value;

        Check.True(difference.IsEmpty);
    }

    [Fact]
    public void Boxes_sharing_only_a_face_do_not_overlap()
    {
        var intersection = Fixtures.Engine.Booleans.Intersect(Fixtures.Cube(0, 1), Fixtures.Cube(1, 1)).Value;
        var union = Fixtures.Engine.Booleans.Union(Fixtures.Cube(0, 1), Fixtures.Cube(1, 1)).Value;

        // Touching faces enclose no volume, so the intersection is genuinely empty
        // rather than a zero-thickness sheet.
        Check.True(intersection.IsEmpty);
        Check.RelativelyClose(2, Fixtures.VolumeOf(union), 1e-9);
        Check.True(Fixtures.TopologyOf(union).IsWatertight);
    }
}

[Suite("Booleans / set identities")]
public sealed class BooleanIdentityTests
{
    private static (IMesh Left, IMesh Right) Operands() =>
        (Fixtures.Sphere(Vec3.Zero, 1, 32), Fixtures.Box(new Vec3(-0.4, -0.4, -2), new Vec3(0.9, 1.5, 0.7)));

    [Fact]
    public void Union_plus_intersection_equals_the_sum_of_the_parts()
    {
        var (left, right) = Operands();

        var union = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Union(left, right).Value);
        var intersection = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Intersect(left, right).Value);

        Check.RelativelyClose(Fixtures.VolumeOf(left) + Fixtures.VolumeOf(right), union + intersection, 1e-6);
    }

    [Fact]
    public void Difference_plus_intersection_equals_the_left_operand()
    {
        var (left, right) = Operands();

        var difference = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Subtract(left, right).Value);
        var intersection = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Intersect(left, right).Value);

        Check.RelativelyClose(Fixtures.VolumeOf(left), difference + intersection, 1e-6);
    }

    [Fact]
    public void Union_gives_the_same_volume_whichever_way_round_the_operands_go()
    {
        var (left, right) = Operands();

        var forwards = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Union(left, right).Value);
        var backwards = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Union(right, left).Value);

        Check.RelativelyClose(forwards, backwards, 1e-6);
    }

    [Fact]
    public void Intersection_gives_the_same_volume_whichever_way_round_the_operands_go()
    {
        var (left, right) = Operands();

        var forwards = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Intersect(left, right).Value);
        var backwards = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Intersect(right, left).Value);

        Check.RelativelyClose(forwards, backwards, 1e-6);
    }

    [Fact]
    public void The_result_never_reaches_outside_the_bounds_of_its_operands()
    {
        var (left, right) = Operands();
        var union = Fixtures.Engine.Booleans.Union(left, right).Value;

        var leftBounds = Fixtures.Engine.Evaluators.GetStatistics(left).Value;
        var rightBounds = Fixtures.Engine.Evaluators.GetStatistics(right).Value;
        var unionBounds = Fixtures.Engine.Evaluators.GetStatistics(union).Value;

        var min = leftBounds.BoundsMin.ComponentMin(rightBounds.BoundsMin);
        var max = leftBounds.BoundsMax.ComponentMax(rightBounds.BoundsMax);

        Check.GreaterOrEqual(unionBounds.BoundsMin.X, min.X - 1e-9);
        Check.LessOrEqual(unionBounds.BoundsMax.Z, max.Z + 1e-9);
    }

    [Fact]
    public void Intersection_is_never_larger_than_either_operand()
    {
        var (left, right) = Operands();
        var intersection = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Intersect(left, right).Value);

        Check.LessOrEqual(intersection, Fixtures.VolumeOf(left) + 1e-9);
        Check.LessOrEqual(intersection, Fixtures.VolumeOf(right) + 1e-9);
    }
}

[Suite("Booleans / curved surfaces")]
public sealed class CurvedBooleanTests
{
    [Fact]
    public void Two_overlapping_spheres_unite_to_the_analytic_volume()
    {
        var left = Fixtures.Sphere(Vec3.Zero, 1, 64);
        var right = Fixtures.Sphere(new Vec3(1, 0, 0), 1, 64);

        var union = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Union(left, right).Value);
        var exact = (2 * Fixtures.SphereVolume(1)) - Fixtures.SphereLensVolume(1, 1);

        Check.RelativelyClose(exact, union, 0.005);
    }

    [Fact]
    public void Two_overlapping_spheres_intersect_to_the_analytic_lens()
    {
        var left = Fixtures.Sphere(Vec3.Zero, 1, 64);
        var right = Fixtures.Sphere(new Vec3(1, 0, 0), 1, 64);

        var lens = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Intersect(left, right).Value);

        Check.RelativelyClose(Fixtures.SphereLensVolume(1, 1), lens, 0.01);
    }

    [Fact]
    public void A_sphere_drilled_out_of_a_box_removes_the_whole_sphere()
    {
        var box = Fixtures.Box(new Vec3(-2, -2, -2), new Vec3(2, 2, 2));
        var sphere = Fixtures.Sphere(Vec3.Zero, 1, 48);

        var drilled = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Subtract(box, sphere).Value);

        Check.RelativelyClose(64 - Fixtures.VolumeOf(sphere), drilled, 1e-6);
    }

    [Fact]
    public void A_cylinder_bored_through_a_box_leaves_a_tube_shaped_hole()
    {
        var box = Fixtures.Box(new Vec3(-1, -1, 0), new Vec3(1, 1, 2));
        var drill = Fixtures.Cylinder(new Vec3(0, 0, -1), 0.5, 4, 128);

        var bored = Fixtures.VolumeOf(Fixtures.Engine.Booleans.Subtract(box, drill).Value);

        Check.RelativelyClose(8 - (Math.PI * 0.25 * 2), bored, 0.002);
    }
}

[Suite("Booleans / topology of results")]
public sealed class BooleanTopologyTests
{
    private static (IMesh Left, IMesh Right) Operands() =>
        (Fixtures.Cube(0, 2), Fixtures.Cube(1, 2));

    [Fact]
    public void A_union_of_boxes_is_watertight()
    {
        var (left, right) = Operands();

        Check.True(Fixtures.TopologyOf(Fixtures.Engine.Booleans.Union(left, right).Value).IsWatertight);
    }

    [Fact]
    public void An_intersection_of_boxes_is_watertight()
    {
        var (left, right) = Operands();

        Check.True(Fixtures.TopologyOf(Fixtures.Engine.Booleans.Intersect(left, right).Value).IsWatertight);
    }

    [Fact]
    public void A_difference_of_boxes_is_watertight()
    {
        var (left, right) = Operands();

        Check.True(Fixtures.TopologyOf(Fixtures.Engine.Booleans.Subtract(left, right).Value).IsWatertight);
    }

    [Fact]
    public void A_box_with_a_spherical_bite_taken_out_of_it_is_watertight()
    {
        var box = Fixtures.Cube(0, 2);
        var sphere = Fixtures.Sphere(Vec3.Zero, 1.2, 24);

        Check.True(Fixtures.TopologyOf(Fixtures.Engine.Booleans.Subtract(box, sphere).Value).IsWatertight);
    }

    [Fact]
    public void A_union_of_spheres_is_watertight()
    {
        var left = Fixtures.Sphere(Vec3.Zero, 1, 24);
        var right = Fixtures.Sphere(new Vec3(1, 0.3, 0), 1, 24);

        Check.True(Fixtures.TopologyOf(Fixtures.Engine.Booleans.Union(left, right).Value).IsWatertight);
    }

    [Fact]
    public void A_result_carries_no_duplicate_vertices_or_slivers()
    {
        var (left, right) = Operands();
        var topology = Fixtures.TopologyOf(Fixtures.Engine.Booleans.Union(left, right).Value);

        Check.Equal(0, topology.DegenerateTriangleCount);
        Check.Equal(0, topology.DuplicateVertexCount);
    }
}

[Suite("Booleans / contract")]
public sealed class BooleanContractTests
{
    [Fact]
    public void An_empty_left_operand_is_refused()
    {
        var union = Fixtures.Engine.Booleans.Union(ImmutableMesh.Empty, Fixtures.UnitCube());

        Check.True(union.IsFailure);
        Check.Equal("Mesh.EmptyOperand", union.Error.Code);
    }

    [Fact]
    public void An_empty_right_operand_is_refused()
    {
        var difference = Fixtures.Engine.Booleans.Subtract(Fixtures.UnitCube(), ImmutableMesh.Empty);

        Check.True(difference.IsFailure);
        Check.Equal("Mesh.EmptyOperand", difference.Error.Code);
    }

    [Fact]
    public void The_operands_are_left_exactly_as_they_were()
    {
        var left = Fixtures.Cube(0, 2);
        var right = Fixtures.Cube(1, 2);
        var leftVertices = left.Vertices;
        var rightTriangles = right.Triangles;

        _ = Fixtures.Engine.Booleans.Union(left, right).Value;

        Check.True(leftVertices.SequenceEqual(left.Vertices));
        Check.True(rightTriangles.SequenceEqual(right.Triangles));
    }

    [Fact]
    public void The_same_operands_always_produce_the_same_mesh()
    {
        var first = Fixtures.Engine.Booleans.Union(Fixtures.Cube(0, 2), Fixtures.Cube(1, 2)).Value;
        var second = Fixtures.Engine.Booleans.Union(Fixtures.Cube(0, 2), Fixtures.Cube(1, 2)).Value;

        Check.True(first.Vertices.SequenceEqual(second.Vertices));
        Check.True(first.Triangles.SequenceEqual(second.Triangles));
    }

    [Fact]
    public void The_result_is_named_after_the_operation_that_made_it()
    {
        var left = Fixtures.Cube(0, 2).WithMetadata(MeshMetadata.Named("plate"));
        var right = Fixtures.Cube(1, 2).WithMetadata(MeshMetadata.Named("stud"));

        var union = Fixtures.Engine.Booleans.Union(left, right).Value;

        Check.Equal("plate Union stud", union.Metadata.Name);

        // CreatedBy names the kernel that produced the result, not merely the feature: the
        // native and managed kernels do not offer the same guarantee, and a caller about to
        // send geometry to a printer needs to be able to tell which one it is holding.
        Check.True(
            union.Metadata.CreatedBy.StartsWith("GeometryEngine.Booleans", StringComparison.Ordinal),
            $"expected the producing kernel to be recorded, but CreatedBy was '{union.Metadata.CreatedBy}'");
    }

    [Fact]
    public void The_result_records_which_kernel_produced_it()
    {
        var left = Fixtures.Cube(0, 2);
        var right = Fixtures.Cube(1, 2);

        var native = BspGeometryEngine.CreateWithManifold().Booleans.Union(left, right).Value;
        var managed = BspGeometryEngine.CreateManagedBsp().Booleans.Union(left, right).Value;

        // Two closed boxes are valid 2-manifolds, so the native kernel takes them without
        // welding and the fallback is not reached.
        Check.Equal("GeometryEngine.Booleans.Manifold", native.Metadata.CreatedBy);
        Check.Equal("GeometryEngine.Booleans", managed.Metadata.CreatedBy);
    }

    [Fact]
    public void A_chain_of_operations_stays_valid()
    {
        var plate = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(4, 4, 1));
        var boss = Fixtures.Cylinder(new Vec3(2, 2, 0), 1, 2, 48);
        var bore = Fixtures.Cylinder(new Vec3(2, 2, -1), 0.5, 5, 48);

        var withBoss = Fixtures.Engine.Booleans.Union(plate, boss).Value;
        var drilled = Fixtures.Engine.Booleans.Subtract(withBoss, bore).Value;

        var expected = (4 * 4 * 1) + (Math.PI * 1 * 1 * 1) - (Math.PI * 0.25 * 2);

        Check.RelativelyClose(expected, Fixtures.VolumeOf(drilled), 0.01);
        Check.True(Fixtures.TopologyOf(drilled).IsWatertight);
    }
}
