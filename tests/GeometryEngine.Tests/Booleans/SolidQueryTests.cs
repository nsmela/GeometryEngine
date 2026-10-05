using System.Diagnostics;
using GeometryEngine.Booleans;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Tests.Booleans;

/// <summary>
/// A <see cref="Solid"/> is a value. None of these tests needs a kernel: they pass before any
/// kernel can evaluate one, which is where the seam between describing and doing belongs.
/// </summary>
[Suite("Booleans / solid query: the description")]
public sealed class SolidDescriptionTests
{
    private static readonly IMesh A = Fixtures.Cube(0, 4);
    private static readonly IMesh B = Fixtures.Cube(1, 2);
    private static readonly IMesh C = Fixtures.Cube(3, 2);

    [Fact]
    public void Fluent_calls_build_the_tree_they_read_as()
    {
        var query = Solid.Of(A).Subtract(B).Union(C);

        var expected = new Solid.Combined(
            BooleanOp.Union,
            new Solid.Combined(BooleanOp.Subtract, new Solid.Leaf(A), new Solid.Leaf(B)),
            new Solid.Leaf(C));

        Check.Equal<Solid>(expected, query);
    }

    [Fact]
    public void Extending_a_description_leaves_the_original_as_it_was()
    {
        var common = Solid.Of(A).Subtract(B);
        var before = Solid.Of(A).Subtract(B);

        var joined = (Solid.Combined)common.Union(C);
        var clipped = (Solid.Combined)common.Intersect(C);

        Check.Equal(before, common);
        Check.True(ReferenceEquals(common, joined.Left));
        Check.True(ReferenceEquals(common, clipped.Left));
        Check.NotEqual<Solid>(joined, clipped);
    }

    [Fact]
    public void A_description_cannot_be_built_around_a_missing_part()
    {
        Check.Throws<ArgumentNullException>(() => Solid.Of(null!));
        Check.Throws<ArgumentNullException>(() => Solid.Of(A).Union((IMesh)null!));
        Check.Throws<ArgumentNullException>(() => Solid.Of(A).Subtract((Solid)null!));
        Check.Throws<ArgumentNullException>(() => new Solid.Combined(BooleanOp.Union, null!, Solid.Of(A)));
    }

    [Fact]
    public void Every_part_is_walked_before_whatever_is_built_from_it()
    {
        var left = Solid.Of(A).Subtract(B);
        var query = left.Union(C);

        var order = SolidWalk.PostOrder(query);

        Check.Equal(5, order.Count);
        Check.True(ReferenceEquals(query, order[^1]));
        Check.True(IndexOf(order, left) < IndexOf(order, query));
        Check.True(IndexOf(order, ((Solid.Combined)left).Left) < IndexOf(order, left));
    }

    [Fact]
    public void A_part_used_twice_is_walked_once()
    {
        var common = Solid.Of(A).Subtract(B);
        var query = common.Union(common.Intersect(C));

        var order = SolidWalk.PostOrder(query);

        // A, B, common, C, the intersection, the union - and common only the once.
        Check.Equal(6, order.Count);
        Check.Equal(1, order.Count(node => ReferenceEquals(node, common)));
    }

    [Fact]
    public void The_meshes_are_listed_once_each_in_reading_order()
    {
        var renamed = B.WithMetadata(MeshMetadata.Named("the same geometry under another name"));
        var query = Solid.Of(A).Subtract(B).Union(C).Subtract(renamed).Intersect(A);

        var leaves = SolidWalk.Leaves(query);

        Check.Equal(3, leaves.Count);
        Check.True(ReferenceEquals(A, leaves[0]));
        Check.True(ReferenceEquals(B, leaves[1]));
        Check.True(ReferenceEquals(C, leaves[2]));
    }

    [Fact]
    public void A_chain_a_hundred_thousand_steps_long_is_walked_without_running_out_of_stack()
    {
        var query = Solid.Of(A);
        for (var i = 0; i < 100_000; i++)
        {
            query = query.Subtract(B);
        }

        Check.Equal(200_001, SolidWalk.PostOrder(query).Count);
        Check.Equal(2, SolidWalk.Leaves(query).Count);
    }

    private static int IndexOf(IReadOnlyList<Solid> order, Solid node)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (ReferenceEquals(order[i], node))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Evaluating a description, on the native kernel and on the managed one it falls back to. The
/// boxes are chosen so every expected volume is arithmetic a reader can do by hand.
/// </summary>
[Suite("Booleans / solid query: evaluation")]
public sealed class SolidEvaluationTests
{
    private static readonly IGeometryEngine Managed = BspGeometryEngine.CreateManagedBsp();

    private static IBooleans Native => Fixtures.Engine.Booleans;

    // 64, with an 8 cavity cut into its top, a 48 block overlapping one side by 16, all clipped
    // to z <= 3:  body - cavity = 56;  + block = 56 + 48 - 16 = 88;  below z = 3 that is
    // (48 - 4) of the body and 24 of the block beyond it = 68.
    private static readonly IMesh Body = Named(Fixtures.Box(new Vec3(0, 0, 0), new Vec3(4, 4, 4)), "body");
    private static readonly IMesh Cavity = Named(Fixtures.Box(new Vec3(1, 1, 2), new Vec3(3, 3, 6)), "cavity");
    private static readonly IMesh Block = Named(Fixtures.Box(new Vec3(3, 0, 0), new Vec3(6, 4, 4)), "block");
    private static readonly IMesh Envelope = Named(Fixtures.Box(new Vec3(-1, -1, -1), new Vec3(7, 5, 3)), "envelope");

    private static Solid Mould => Solid.Of(Body).Subtract(Cavity).Union(Block).Intersect(Envelope);

    [Fact]
    public void A_mixed_description_evaluates_to_the_solid_it_describes()
    {
        var mould = Native.Evaluate(Mould).Value;

        Check.RelativelyClose(68, Fixtures.VolumeOf(mould), 1e-9);
        Check.True(Fixtures.TopologyOf(mould).IsWatertight);
        Check.Equal(ManifoldBooleanOperations.NativeProducer, mould.Metadata.CreatedBy);
    }

    [Fact]
    public void It_is_the_same_solid_the_steps_give_taken_one_at_a_time()
    {
        var a = Fixtures.Sphere(Vec3.Zero, 10);
        var b = Fixtures.Sphere(new Vec3(6, 0, 0), 8);
        var c = Fixtures.Cylinder(new Vec3(0, 0, -15), 3, 30);
        var d = Fixtures.Sphere(new Vec3(0, 4, 0), 11);

        var stepwise = Native.Intersect(Native.Subtract(Native.Union(a, b).Value, c).Value, d).Value;
        var queried = Native.Evaluate(Solid.Of(a).Union(b).Subtract(c).Intersect(d)).Value;

        Check.RelativelyClose(Fixtures.VolumeOf(stepwise), Fixtures.VolumeOf(queried), 1e-9);
        Check.RelativelyClose(
            Fixtures.Engine.Evaluators.GetStatistics(stepwise).Value.SurfaceArea,
            Fixtures.Engine.Evaluators.GetStatistics(queried).Value.SurfaceArea,
            1e-9);
        Check.True(Fixtures.TopologyOf(queried).IsWatertight);
    }

    [Fact]
    public void Each_mesh_is_read_into_the_kernel_once_however_often_it_is_used()
    {
        var renamed = Cavity.WithMetadata(MeshMetadata.Named("the cavity again"));
        var query = Solid.Of(Body).Subtract(Cavity).Union(Block).Subtract(renamed).Intersect(Body);

        var evaluation = ManifoldKernel.Evaluate(query, MeshMetadata.Named("probe")).Value;

        // Five leaves, three meshes: the body twice, and the cavity under two names.
        Check.Equal(3, evaluation.MeshesImported);
        Check.RelativelyClose(56, Fixtures.VolumeOf(evaluation.Outcome.Mesh), 1e-9);
    }

    [Fact]
    public void A_part_shared_between_two_branches_gives_the_right_solid()
    {
        var common = Solid.Of(Body).Subtract(Cavity);

        // What the two share is all of the first, so their union is the first again.
        var result = Native.Evaluate(common.Union(common.Intersect(Envelope))).Value;

        Check.RelativelyClose(56, Fixtures.VolumeOf(result), 1e-9);
        Check.True(Fixtures.TopologyOf(result).IsWatertight);
    }

    [Fact]
    public void A_description_that_is_one_mesh_comes_back_as_that_mesh()
    {
        Check.True(ReferenceEquals(Body, Native.Evaluate(Solid.Of(Body)).Value));
        Check.True(ReferenceEquals(Body, Managed.Booleans.Evaluate(Solid.Of(Body)).Value));
    }

    [Fact]
    public void A_description_of_nothing_evaluates_to_an_empty_mesh()
    {
        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var nothing = booleans.Evaluate(Solid.Of(Body).Subtract(Body));

            Check.True(nothing.IsSuccess);
            Check.True(nothing.Value.IsEmpty);
        }
    }

    [Fact]
    public void A_step_that_leaves_nothing_does_not_stop_the_steps_after_it()
    {
        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var result = booleans.Evaluate(Solid.Of(Body).Subtract(Body).Union(Block).Subtract(Cavity)).Value;

            Check.RelativelyClose(48, Fixtures.VolumeOf(result), 1e-6);
        }
    }

    [Fact]
    public void A_mesh_with_no_geometry_is_refused_before_anything_runs()
    {
        var query = Solid.Of(Body).Subtract(ImmutableMesh.Empty).Union(Block);

        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var result = booleans.Evaluate(query);

            Check.True(result.IsFailure);
            Check.Equal(MeshErrors.EmptyOperand, result.Error);
        }
    }

    [Fact]
    public void A_mesh_the_kernel_will_not_take_is_named_in_the_failure()
    {
        var withoutFallback = new ManifoldBooleanOperations();

        var result = withoutFallback.Evaluate(Solid.Of(Body).Subtract(Cavity).Union(OpenBox("lidless")));

        Check.True(result.IsFailure);
        Check.Equal("Manifold.InvalidMesh", result.Error.Code);
        Check.True(result.Error.Description.Contains("lidless", StringComparison.Ordinal));
    }

    [Fact]
    public void What_the_native_kernel_declines_is_evaluated_by_the_managed_one_and_says_so()
    {
        var result = Native.Evaluate(Solid.Of(Body).Subtract(Cavity).Union(OpenBox("lidless")));

        Check.True(result.IsSuccess);
        Check.Equal(ManifoldBooleanOperations.FallbackProducer, result.Value.Metadata.CreatedBy);
    }

    [Fact]
    public void The_managed_kernel_evaluates_the_same_description_to_the_same_solid()
    {
        var mould = Managed.Booleans.Evaluate(Mould).Value;

        Check.RelativelyClose(68, Fixtures.VolumeOf(mould), 1e-6);
        Check.True(Fixtures.TopologyOf(mould).IsClosed);
    }

    [Fact]
    public void One_step_is_named_as_the_pairwise_call_names_it()
    {
        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var result = booleans.Evaluate(Solid.Of(Body).Subtract(Cavity)).Value;

            Check.Equal(booleans.Subtract(Body, Cavity).Value.Metadata.Name, result.Metadata.Name);
            Check.Equal("body Subtract cavity", result.Metadata.Name);
        }
    }

    [Fact]
    public void A_longer_description_is_named_for_its_subject_and_a_count()
    {
        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            Check.Equal("body combined with 3 meshes", booleans.Evaluate(Mould).Value.Metadata.Name);
        }
    }

    [Fact]
    public void A_description_composes_with_results_through_Map_and_Bind()
    {
        var mould = Fixtures.Engine.Generators.GenerateBox(new Vec3(0, 0, 0), new Vec3(4, 4, 4))
            .Map(Solid.Of)
            .Map(body => body.Subtract(Cavity).Union(Block))
            .Bind(Native.Evaluate);

        Check.True(mould.IsSuccess);
        Check.RelativelyClose(88, Fixtures.VolumeOf(mould.Value), 1e-9);
    }

    [Fact]
    public void A_description_that_fails_part_way_frees_what_it_had_already_read_in()
    {
        var withoutFallback = new ManifoldBooleanOperations();
        var a = Fixtures.Sphere(Vec3.Zero, 10, 96);
        var b = Fixtures.Sphere(new Vec3(5, 0, 0), 10, 96);
        var failing = Solid.Of(a).Subtract(b).Union(OpenBox("lidless"));

        for (var i = 0; i < 20; i++)
        {
            Check.True(withoutFallback.Evaluate(failing).IsFailure);
        }

        var before = PrivateBytes();
        for (var i = 0; i < 400; i++)
        {
            Check.True(withoutFallback.Evaluate(failing).IsFailure);
        }

        var retained = PrivateBytes() - before;

        // Both spheres are inside the kernel by the time the open box is refused. Leaving them
        // there would retain a few megabytes a run, several hundred over the four hundred.
        const long Ceiling = 64L * 1024 * 1024;
        Check.True(retained <= Ceiling, $"Retained {retained / 1024}KB over 400 failing descriptions (ceiling {Ceiling / 1024}KB)");
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static IMesh Named(IMesh mesh, string name) => mesh.WithMetadata(mesh.Metadata.WithName(name));

    /// <summary>A box with its last two triangles left off: an open surface, which Manifold refuses.</summary>
    private static IMesh OpenBox(string name)
    {
        var box = Fixtures.Box(new Vec3(10, 10, 10), new Vec3(12, 12, 12));
        return Fixtures.Engine.CreateMesh(box.Vertices, box.Triangles[..^6], MeshMetadata.Named(name)).Value;
    }
}
