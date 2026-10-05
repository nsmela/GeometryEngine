using System.Runtime.CompilerServices;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Tests.Booleans;

/// <summary>
/// Reading a mesh into the native kernel costs about what a boolean on it costs. Under
/// <see cref="SolidRetention.Keep"/> the solid read in stays with the mesh, and so does the one
/// behind each result, so neither is read in a second time.
///
/// Every test builds its own meshes: what is kept is kept with a mesh, so a mesh shared between
/// tests would make each one's answer depend on which ran first.
/// </summary>
[Suite("Booleans / solids kept in the kernel")]
public sealed class RetainedSolidTests
{
    private static readonly MeshMetadata Probe = MeshMetadata.Named("probe");

    // 64, an 8 cavity cut into its top, and a block overlapping one side by 16.
    private static IMesh Body() => Fixtures.Box(new Vec3(0, 0, 0), new Vec3(4, 4, 4));

    private static IMesh Cavity() => Fixtures.Box(new Vec3(1, 1, 2), new Vec3(3, 3, 6));

    private static IMesh Block() => Fixtures.Box(new Vec3(3, 0, 0), new Vec3(6, 4, 4));

    private static ManifoldEvaluation Keeping(Solid query) =>
        ManifoldKernel.Evaluate(query, Probe, SolidRetention.Keep).Value;

    [Fact]
    public void A_mesh_read_in_once_is_not_read_in_again()
    {
        var (body, cavity) = (Body(), Cavity());
        var query = Solid.Of(body).Subtract(cavity);

        var first = Keeping(query);
        var second = Keeping(query);

        Check.Equal(2, first.MeshesImported);
        Check.Equal(0, second.MeshesImported);
        Check.RelativelyClose(56, Fixtures.VolumeOf(first.Outcome.Mesh), 1e-9);
        Check.RelativelyClose(56, Fixtures.VolumeOf(second.Outcome.Mesh), 1e-9);
    }

    [Fact]
    public void A_result_is_already_in_the_kernel_when_it_is_used_next()
    {
        var cut = Keeping(Solid.Of(Body()).Subtract(Cavity())).Outcome.Mesh;

        var next = Keeping(Solid.Of(cut).Subtract(Block()));

        // Only the block is new to the kernel; the cut body never left it.
        Check.Equal(1, next.MeshesImported);
        Check.RelativelyClose(40, Fixtures.VolumeOf(next.Outcome.Mesh), 1e-9);
        Check.True(Fixtures.TopologyOf(next.Outcome.Mesh).IsWatertight);
    }

    [Fact]
    public void A_copy_with_new_metadata_shares_what_was_read_in()
    {
        var (body, cavity) = (Body(), Cavity());
        _ = Keeping(Solid.Of(body).Subtract(cavity));

        var renamed = body.WithMetadata(MeshMetadata.Named("the body again"));
        var again = Keeping(Solid.Of(renamed).Subtract(cavity));

        Check.Equal(0, again.MeshesImported);
    }

    [Fact]
    public void A_moved_mesh_is_read_in_afresh()
    {
        var (body, cavity) = (Body(), Cavity());
        _ = Keeping(Solid.Of(body).Subtract(cavity));

        // What is kept is the solid where it stood; this one stands somewhere else.
        var moved = Fixtures.Engine.Transforms.Translate(body, new Vec3(2, 0, 0)).Value;
        var again = Keeping(Solid.Of(moved).Subtract(cavity));

        Check.Equal(1, again.MeshesImported);
        Check.RelativelyClose(60, Fixtures.VolumeOf(again.Outcome.Mesh), 1e-9);
    }

    [Fact]
    public void Asked_to_keep_nothing_the_kernel_reads_every_mesh_in_each_time()
    {
        var (body, cavity) = (Body(), Cavity());
        var query = Solid.Of(body).Subtract(cavity);
        var before = SettledLive();

        var first = ManifoldKernel.Evaluate(query, Probe, SolidRetention.None).Value;
        var second = ManifoldKernel.Evaluate(query, Probe, SolidRetention.None).Value;

        Check.Equal(2, first.MeshesImported);
        Check.Equal(2, second.MeshesImported);
        Check.Equal(before, RetainedSolid.Live);
        GC.KeepAlive((body, cavity, first, second));
    }

    [Fact]
    public void A_chain_through_kept_solids_gives_the_solid_reading_each_step_in_gives()
    {
        IMesh[] Parts() =>
        [
            Fixtures.Sphere(Vec3.Zero, 10),
            Fixtures.Sphere(new Vec3(6, 0, 0), 8),
            Fixtures.Cylinder(new Vec3(0, 0, -15), 3, 30),
            Fixtures.Sphere(new Vec3(0, 4, 0), 11),
        ];

        IMesh Chain(IBooleans booleans, IMesh[] p) =>
            booleans.Intersect(booleans.Subtract(booleans.Union(p[0], p[1]).Value, p[2]).Value, p[3]).Value;

        var kept = Chain(BspGeometryEngine.CreateWithManifold(SolidRetention.Keep).Booleans, Parts());
        var reread = Chain(BspGeometryEngine.CreateWithManifold(SolidRetention.None).Booleans, Parts());

        Check.RelativelyClose(Fixtures.VolumeOf(reread), Fixtures.VolumeOf(kept), 1e-9);
        Check.RelativelyClose(
            Fixtures.Engine.Evaluators.GetStatistics(reread).Value.SurfaceArea,
            Fixtures.Engine.Evaluators.GetStatistics(kept).Value.SurfaceArea,
            1e-9);
        Check.True(Fixtures.TopologyOf(kept).IsWatertight);
    }

    [Fact]
    public void One_solid_is_kept_for_each_mesh_read_in_and_each_result()
    {
        var (body, cavity) = (Body(), Cavity());
        var query = Solid.Of(body).Subtract(cavity);
        var before = SettledLive();

        var first = Keeping(query);
        Check.Equal(before + 3, RetainedSolid.Live);

        // Nothing new is read in the second time, so only its result adds one.
        var second = Keeping(query);
        Check.Equal(before + 4, RetainedSolid.Live);

        GC.KeepAlive((body, cavity, first, second));
    }

    [Fact]
    public void A_batch_and_a_split_keep_what_they_read_in_and_what_they_build()
    {
        var (body, block) = (Body(), Block());
        var before = SettledLive();

        var union = ManifoldKernel.Batch([body, block], ManifoldOpType.Add, Probe, SolidRetention.Keep).Value;
        Check.Equal(before + 3, RetainedSolid.Live);

        // The body is already there; the two halves are new.
        var halves = ManifoldKernel.Split(
            body, Plane.FromNormalAndPoint(Direction.Z, new Vec3(2, 2, 2)), Probe, Probe, SolidRetention.Keep).Value;
        Check.Equal(before + 5, RetainedSolid.Live);

        Check.RelativelyClose(96, Fixtures.VolumeOf(union.Mesh), 1e-9);
        Check.RelativelyClose(32, Fixtures.VolumeOf(halves.Front.Mesh), 1e-9);
        GC.KeepAlive((body, block, union, halves));
    }

    [Fact]
    public void The_default_engine_keeps_solids_and_one_asked_not_to_does_not()
    {
        var (body, cavity) = (Body(), Cavity());
        var before = SettledLive();

        var plain = BspGeometryEngine.CreateWithManifold(SolidRetention.None).Booleans.Subtract(body, cavity).Value;
        Check.Equal(before, RetainedSolid.Live);

        var kept = BspGeometryEngine.CreateWithManifold().Booleans.Subtract(body, cavity).Value;
        Check.Equal(before + 3, RetainedSolid.Live);

        GC.KeepAlive((body, cavity, plain, kept));
    }

    [Fact]
    public void What_is_kept_is_let_go_when_its_mesh_is_collected()
    {
        var before = SettledLive();

        var during = UseAndDrop();

        Check.Greater(during, before);
        Check.Equal(before, SettledLive());
    }

    [Fact]
    public void Many_threads_cutting_one_kept_mesh_get_the_answers_one_thread_gets()
    {
        var body = Fixtures.Sphere(Vec3.Zero, 10);
        var tools = Enumerable.Range(0, 4)
            .Select(i => Fixtures.Sphere(new Vec3(8 * Math.Cos(i * 1.5), 8 * Math.Sin(i * 1.5), i - 1.5), 4, 24))
            .ToArray();

        var expected = tools
            .Select(tool => Fixtures.VolumeOf(
                ManifoldKernel.Evaluate(Solid.Of(body).Subtract(tool), Probe, SolidRetention.None).Value.Outcome.Mesh))
            .ToArray();

        // The first use is made by every thread at once, so reading the meshes in is raced too.
        var volumes = new double[32];
        using var start = new Barrier(8);
        var threads = Enumerable.Range(0, 8)
            .Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                for (var i = t; i < volumes.Length; i += 8)
                {
                    var cut = Keeping(Solid.Of(body).Subtract(tools[i % tools.Length])).Outcome.Mesh;
                    volumes[i] = Fixtures.VolumeOf(cut);
                }
            }))
            .ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        for (var i = 0; i < volumes.Length; i++)
        {
            Check.RelativelyClose(expected[i % tools.Length], volumes[i], 1e-9);
        }

        // However the race went, one solid is kept per mesh: a later use reads nothing in.
        Check.Equal(0, Keeping(Solid.Of(body).Subtract(tools[0])).MeshesImported);
    }

    /// <summary>
    /// Builds meshes, uses them, and returns with nothing left referring to them. Kept out of
    /// line so no local of the caller's can hold one alive past the call.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long UseAndDrop()
    {
        var cut = Keeping(Solid.Of(Body()).Subtract(Cavity())).Outcome.Mesh;
        _ = Keeping(Solid.Of(cut).Subtract(Block()));
        return RetainedSolid.Live;
    }

    private static long SettledLive()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return RetainedSolid.Live;
    }
}
