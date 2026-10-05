using GeometryEngine.Booleans;
using GeometryEngine.Internal;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Tests.Booleans;

/// <summary>
/// A part of a description can be moved, turned or resized inside it. The kernel then moves the
/// solid it already has, where moving the mesh beforehand would hand it a new mesh to read in.
/// </summary>
[Suite("Booleans / solid query: moved parts")]
public sealed class SolidTransformTests
{
    private static readonly MeshMetadata Probe = MeshMetadata.Named("probe");
    private static readonly IGeometryEngine Managed = BspGeometryEngine.CreateManagedBsp();

    private static IBooleans Native => Fixtures.Engine.Booleans;

    private static IGeometryTransforms Transforms => Fixtures.Engine.Transforms;

    // 64, an 8 cavity cut into its top, and a 48 block overlapping one side by 16.
    private static IMesh Body() => Fixtures.Box(new Vec3(0, 0, 0), new Vec3(4, 4, 4));

    private static IMesh Cavity() => Fixtures.Box(new Vec3(1, 1, 2), new Vec3(3, 3, 6));

    private static IMesh Block() => Fixtures.Box(new Vec3(3, 0, 0), new Vec3(6, 4, 4));

    [Fact]
    public void Fluent_calls_build_the_moved_parts_they_read_as()
    {
        var body = Body();
        var turn = Rotation.FromAxisAngle(Direction.Z, 0.5);

        var query = Solid.Of(body).Translate(new Vec3(1, 2, 3)).Rotate(turn).Scale(new Vec3(2, 2, 2));

        var scaled = (Solid.Transformed)query;
        var turned = (Solid.Transformed)scaled.Source;
        var shifted = (Solid.Transformed)turned.Source;
        Check.Equal(new SolidTransform.Scaling(new Vec3(2, 2, 2)), scaled.Transform);
        Check.Equal(new SolidTransform.Turn(turn), turned.Transform);
        Check.Equal(new SolidTransform.Translation(new Vec3(1, 2, 3)), shifted.Transform);
        Check.True(ReferenceEquals(body, ((Solid.Leaf)shifted.Source).Mesh));
    }

    [Fact]
    public void A_moved_part_is_walked_after_what_it_moves_and_its_mesh_is_listed()
    {
        var (body, cavity) = (Body(), Cavity());
        var leaf = Solid.Of(body);
        var moved = leaf.Translate(new Vec3(2, 0, 0));
        var query = moved.Subtract(cavity);

        var order = SolidWalk.PostOrder(query);
        var uses = SolidWalk.Uses(order, query);

        Check.Equal(4, order.Count);
        Check.True(IndexOf(order, leaf) < IndexOf(order, moved));
        Check.True(IndexOf(order, moved) < IndexOf(order, query));
        Check.Equal(1, uses.OfNode[leaf]);
        Check.Equal(1, uses.OfNode[moved]);
        Check.Equal(2, SolidWalk.Leaves(query).Count);
        Check.True(ReferenceEquals(body, SolidWalk.Leaves(query)[0]));
    }

    [Fact]
    public void A_part_moved_a_hundred_thousand_times_is_walked_without_running_out_of_stack()
    {
        var query = Solid.Of(Body());
        for (var i = 0; i < 100_000; i++)
        {
            query = query.Translate(new Vec3(1e-6, 0, 0));
        }

        Check.Equal(100_001, SolidWalk.PostOrder(query).Count);
    }

    [Fact]
    public void A_moved_body_less_a_cavity_is_the_solid_it_describes_on_both_kernels()
    {
        // The body moved two along x meets only half the cavity: 64 - 4.
        var query = Solid.Of(Body()).Translate(new Vec3(2, 0, 0)).Subtract(Cavity());

        var native = Native.Evaluate(query).Value;
        var managed = Managed.Booleans.Evaluate(query).Value;

        Check.RelativelyClose(60, Fixtures.VolumeOf(native), 1e-9);
        Check.True(Fixtures.TopologyOf(native).IsWatertight);
        Check.Equal(ManifoldBooleanOperations.NativeProducer, native.Metadata.CreatedBy);
        Check.RelativelyClose(60, Fixtures.VolumeOf(managed), 1e-6);
    }

    [Fact]
    public void Moving_a_part_in_the_description_gives_the_solid_moving_its_mesh_first_gives()
    {
        var sphere = Fixtures.Sphere(new Vec3(3, 0, 0), 10);
        var tool = Fixtures.Cylinder(new Vec3(0, 0, -15), 3, 30);
        var axis = Direction.From(new Vec3(1, 2, 3)).Value;
        var cases = new (string Name, Func<Solid, Solid> InDescription, Func<IMesh, IMesh> Beforehand)[]
        {
            ("translate", s => s.Translate(new Vec3(1.5, -2, 0.25)), m => Transforms.Translate(m, new Vec3(1.5, -2, 0.25)).Value),
            ("rotate about an axis", s => s.Rotate(axis, 0.7), m => Transforms.Rotate(m, axis, 0.7).Value),
            ("rotate", s => s.Rotate(Rotation.FromAxisAngle(Direction.Y, -1.1)), m => Transforms.Rotate(m, Rotation.FromAxisAngle(Direction.Y, -1.1)).Value),
            ("scale", s => s.Scale(new Vec3(1, 2, 0.5)), m => Transforms.Scale(m, new Vec3(1, 2, 0.5)).Value),
        };

        foreach (var (name, inDescription, beforehand) in cases)
        {
            var described = Native.Evaluate(inDescription(Solid.Of(tool)).Union(sphere)).Value;
            var moved = Native.Union(beforehand(tool), sphere).Value;

            var (a, b) = (Statistics(described), Statistics(moved));
            Check.True(Math.Abs(a.Volume - b.Volume) <= 1e-9 * b.Volume, $"{name}: volume {a.Volume} against {b.Volume}");
            Check.True(Math.Abs(a.SurfaceArea - b.SurfaceArea) <= 1e-9 * b.SurfaceArea, $"{name}: area {a.SurfaceArea} against {b.SurfaceArea}");
            Check.True(a.BoundsMin.DistanceTo(b.BoundsMin) <= 1e-9, $"{name}: lower bound {a.BoundsMin} against {b.BoundsMin}");
            Check.True(a.BoundsMax.DistanceTo(b.BoundsMax) <= 1e-9, $"{name}: upper bound {a.BoundsMax} against {b.BoundsMax}");
        }
    }

    [Fact]
    public void Moves_are_applied_in_the_order_they_are_written()
    {
        var quarter = Rotation.FromAxisAngle(Direction.Z, Math.PI / 2);
        var shift = new Vec3(10, 0, 0);

        // The body spans 0..4 on x. Shifted then turned a quarter about z it lies along +y;
        // turned then shifted it stays beside the x axis.
        var shiftedThenTurned = Statistics(Native.Evaluate(Solid.Of(Body()).Translate(shift).Rotate(quarter)).Value);
        var turnedThenShifted = Statistics(Native.Evaluate(Solid.Of(Body()).Rotate(quarter).Translate(shift)).Value);

        Check.True(shiftedThenTurned.BoundsMin.DistanceTo(new Vec3(-4, 10, 0)) <= 1e-9);
        Check.True(turnedThenShifted.BoundsMin.DistanceTo(new Vec3(6, 0, 0)) <= 1e-9);
    }

    [Fact]
    public void A_combined_part_can_be_moved_as_a_whole()
    {
        var body = Body();
        var query = Solid.Of(body).Subtract(Cavity()).Translate(new Vec3(10, 0, 0)).Union(body);

        foreach (var (booleans, tolerance) in new[] { (Native, 1e-9), (Managed.Booleans, 1e-6) })
        {
            // The cut body, moved clear of the whole one: 56 + 64.
            Check.RelativelyClose(120, Fixtures.VolumeOf(booleans.Evaluate(query).Value), tolerance);
        }
    }

    [Fact]
    public void A_resized_part_has_its_volume_multiplied_by_the_factors()
    {
        var query = Solid.Of(Body()).Subtract(Cavity()).Scale(new Vec3(2, 2, 2));

        Check.RelativelyClose(448, Fixtures.VolumeOf(Native.Evaluate(query).Value), 1e-9);
        Check.RelativelyClose(448, Fixtures.VolumeOf(Managed.Booleans.Evaluate(query).Value), 1e-6);
    }

    [Fact]
    public void A_scale_that_would_turn_a_solid_inside_out_is_refused_before_anything_runs()
    {
        var mirrored = Solid.Of(Body()).Scale(new Vec3(1, -1, 1)).Subtract(Cavity());
        var endless = Solid.Of(Body()).Translate(new Vec3(double.NaN, 0, 0)).Subtract(Cavity());

        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            Check.Equal("Transforms.MirroringScale", booleans.Evaluate(mirrored).Error.Code);
            Check.Equal("Transforms.NonFinite", booleans.Evaluate(endless).Error.Code);
        }
    }

    [Fact]
    public void A_kept_mesh_moved_inside_a_description_is_not_read_in_again()
    {
        var (body, cavity) = (Body(), Cavity());
        _ = ManifoldKernel.Evaluate(Solid.Of(body).Subtract(cavity), Probe, SolidRetention.Keep).Value;

        var moved = ManifoldKernel.Evaluate(
            Solid.Of(body).Translate(new Vec3(2, 0, 0)).Subtract(cavity), Probe, SolidRetention.Keep).Value;

        Check.Equal(0, moved.MeshesImported);
        Check.Equal(1, moved.HandlesHeldAtRead);
        Check.RelativelyClose(60, Fixtures.VolumeOf(moved.Outcome.Mesh), 1e-9);
    }

    [Fact]
    public void A_description_that_only_moves_a_mesh_keeps_the_meshs_name()
    {
        var body = Body().WithMetadata(MeshMetadata.Named("the body"));

        foreach (var booleans in new[] { Native, Managed.Booleans })
        {
            var moved = booleans.Evaluate(Solid.Of(body).Translate(new Vec3(0, 0, 5))).Value;

            Check.Equal("the body", moved.Metadata.Name);
            Check.RelativelyClose(64, Fixtures.VolumeOf(moved), 1e-9);
            Check.True(Statistics(moved).BoundsMin.DistanceTo(new Vec3(0, 0, 5)) <= 1e-9);
        }
    }

    [Fact]
    public void A_part_that_is_nothing_is_still_nothing_wherever_it_is_moved()
    {
        var body = Body();
        var query = Solid.Of(body).Subtract(body).Translate(new Vec3(1, 1, 1)).Union(Block());

        foreach (var (booleans, tolerance) in new[] { (Native, 1e-9), (Managed.Booleans, 1e-6) })
        {
            Check.RelativelyClose(48, Fixtures.VolumeOf(booleans.Evaluate(query).Value), tolerance);
        }
    }

    [Fact]
    public void A_description_that_fails_after_moving_parts_frees_the_moved_solids()
    {
        var withoutFallback = new ManifoldBooleanOperations();
        var a = Fixtures.Sphere(Vec3.Zero, 10, 96);
        var b = Fixtures.Sphere(new Vec3(5, 0, 0), 10, 96);
        var box = Fixtures.Box(new Vec3(10, 10, 10), new Vec3(12, 12, 12));
        var lidless = Fixtures.Engine.CreateMesh(box.Vertices, box.Triangles[..^6], MeshMetadata.Named("lidless")).Value;

        // Each moved part is a handle of its own holding the sphere it moves, and all three exist
        // by the time the open box is refused.
        var failing = Solid.Of(a).Translate(new Vec3(1, 0, 0))
            .Subtract(Solid.Of(b).Rotate(Direction.Z, 0.3))
            .Scale(new Vec3(1.5, 1, 1))
            .Union(lidless);

        long PrivateBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.PrivateMemorySize64;
        }

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
        const long Ceiling = 64L * 1024 * 1024;
        Check.True(retained <= Ceiling, $"Retained {retained / 1024}KB over 400 failing descriptions (ceiling {Ceiling / 1024}KB)");
    }

    private static MeshStatistics Statistics(IMesh mesh) => Fixtures.Engine.Evaluators.GetStatistics(mesh).Value;

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
