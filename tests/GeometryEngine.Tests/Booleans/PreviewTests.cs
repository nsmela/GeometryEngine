using GeometryEngine.Internal.Native;

namespace GeometryEngine.Tests.Booleans;

/// <summary>
/// A description shown half way. The preview is a description of its own, evaluated to a mesh
/// the caller can look at; the rest is then built on that mesh, which the kernel still holds.
/// A caller keeping previews it is not about to build on can release what the kernel holds for
/// them and keep the meshes.
/// </summary>
[Suite("Booleans / previews and releasing what is kept")]
public sealed class PreviewTests
{
    private static readonly MeshMetadata Probe = MeshMetadata.Named("probe");

    private static IBooleans Keeping => BspGeometryEngine.CreateWithManifold(SolidRetention.Keep).Booleans;

    // 64, an 8 cavity cut into its top, and a 48 block overlapping one side by 16.
    private static IMesh Body() => Fixtures.Box(new Vec3(0, 0, 0), new Vec3(4, 4, 4));

    private static IMesh Cavity() => Fixtures.Box(new Vec3(1, 1, 2), new Vec3(3, 3, 6));

    private static IMesh Block() => Fixtures.Box(new Vec3(3, 0, 0), new Vec3(6, 4, 4));

    [Fact]
    public void The_rest_built_on_a_preview_reads_in_only_what_is_new_and_gives_the_whole()
    {
        var (body, cavity, block) = (Body(), Cavity(), Block());

        var preview = ManifoldKernel.Evaluate(Solid.Of(body).Subtract(cavity), Probe, SolidRetention.Keep).Value;
        var rest = ManifoldKernel.Evaluate(Solid.Of(preview.Outcome.Mesh).Subtract(block), Probe, SolidRetention.Keep).Value;
        var whole = ManifoldKernel.Evaluate(Solid.Of(body).Subtract(cavity).Subtract(block), Probe, SolidRetention.None).Value;

        Check.Equal(1, rest.MeshesImported);
        Check.RelativelyClose(Fixtures.VolumeOf(whole.Outcome.Mesh), Fixtures.VolumeOf(rest.Outcome.Mesh), 1e-9);
        Check.RelativelyClose(40, Fixtures.VolumeOf(rest.Outcome.Mesh), 1e-9);
        Check.True(Fixtures.TopologyOf(rest.Outcome.Mesh).IsWatertight);
    }

    [Fact]
    public void A_preview_can_be_read_on_one_thread_while_the_rest_is_cut_on_another()
    {
        var booleans = Keeping;
        var preview = booleans.Evaluate(
            Solid.Of(Fixtures.Sphere(Vec3.Zero, 10)).Subtract(Fixtures.Sphere(new Vec3(8, 0, 0), 4, 24))).Value;
        var tool = Fixtures.Sphere(new Vec3(-8, 0, 0), 4, 24);
        var expected = Fixtures.VolumeOf(
            ManifoldKernel.Evaluate(Solid.Of(preview).Subtract(tool), Probe, SolidRetention.None).Value.Outcome.Mesh);

        static double Sum(IMesh mesh) => mesh.Vertices.Sum(vertex => vertex.X + vertex.Y + vertex.Z);

        var before = Sum(preview);
        var stop = false;
        var steady = true;
        var reader = new Thread(() =>
        {
            // What a renderer does: walk the vertices, again and again.
            while (!Volatile.Read(ref stop))
            {
                steady &= Sum(preview) == before;
            }
        });

        reader.Start();
        var volumes = Enumerable.Range(0, 6)
            .Select(_ => Fixtures.VolumeOf(booleans.Evaluate(Solid.Of(preview).Subtract(tool)).Value))
            .ToArray();
        Volatile.Write(ref stop, true);
        Check.True(reader.Join(TimeSpan.FromSeconds(60)));

        Check.True(steady);
        foreach (var volume in volumes)
        {
            Check.RelativelyClose(expected, volume, 1e-9);
        }
    }

    [Fact]
    public void Releasing_a_kept_mesh_lets_its_solid_go_while_the_mesh_lives()
    {
        var booleans = Keeping;
        var (body, cavity) = (Body(), Cavity());
        var before = SettledLive();
        var cut = booleans.Subtract(body, cavity).Value;
        Check.Equal(before + 3, RetainedSolid.Live);

        Check.True(booleans.Release(cut));
        Check.Equal(before + 2, RetainedSolid.Live);

        // Nothing is kept for it now, so there is nothing more to let go.
        Check.False(booleans.Release(cut));
        Check.RelativelyClose(56, Fixtures.VolumeOf(cut), 1e-9);
        GC.KeepAlive((body, cavity, cut));
    }

    [Fact]
    public void A_released_mesh_is_read_in_again_when_it_is_next_used()
    {
        var booleans = Keeping;
        var (body, cavity, block) = (Body(), Cavity(), Block());
        _ = booleans.Subtract(body, cavity).Value;

        Check.True(booleans.Release(body));
        var again = ManifoldKernel.Evaluate(Solid.Of(body).Subtract(block), Probe, SolidRetention.Keep).Value;

        // The body and the block, which is new; the cavity is not part of this.
        Check.Equal(2, again.MeshesImported);
        Check.RelativelyClose(48, Fixtures.VolumeOf(again.Outcome.Mesh), 1e-9);
    }

    [Fact]
    public void Releasing_one_copy_releases_what_all_its_copies_share()
    {
        var booleans = Keeping;
        var body = Body();
        Check.True(booleans.Prepare(body).IsSuccess);

        var renamed = body.WithMetadata(MeshMetadata.Named("the body again"));

        Check.True(booleans.Release(renamed));
        Check.False(booleans.Release(body));
    }

    [Fact]
    public void Releasing_what_was_never_kept_does_nothing()
    {
        var body = Body();

        Check.False(Keeping.Release(body));
        Check.False(BspGeometryEngine.CreateWithManifold(SolidRetention.None).Booleans.Release(body));
        Check.False(BspGeometryEngine.CreateManagedBsp().Booleans.Release(body));
        Check.False(Keeping.Release(ImmutableMesh.Empty));
    }

    [Fact]
    public void Previews_held_in_a_stack_keep_a_solid_each_until_they_are_released()
    {
        var booleans = Keeping;
        var (body, cavity) = (Body(), Cavity());
        Check.True(booleans.Prepare(body).IsSuccess);
        Check.True(booleans.Prepare(cavity).IsSuccess);
        var before = SettledLive();

        // An undo stack: every state the user has seen, each built on the same two meshes.
        var stack = Enumerable.Range(0, 6)
            .Select(i => booleans.Evaluate(Solid.Of(body).Subtract(Solid.Of(cavity).Translate(new Vec3(0, 0, i * 0.1)))).Value)
            .ToList();
        Check.Equal(before + 6, RetainedSolid.Live);

        // All but the one on screen are let go; every mesh in the stack is still there to show.
        foreach (var preview in stack.SkipLast(1))
        {
            Check.True(booleans.Release(preview));
        }

        Check.Equal(before + 1, RetainedSolid.Live);
        Check.True(stack.All(preview => Fixtures.TopologyOf(preview).IsWatertight));
        GC.KeepAlive((body, cavity, stack));
    }

    [Fact]
    public void A_mesh_can_be_released_while_other_threads_are_cutting_it()
    {
        var booleans = Keeping;
        var body = Fixtures.Sphere(Vec3.Zero, 10);
        var tool = Fixtures.Sphere(new Vec3(8, 0, 0), 4, 24);
        var expected = Fixtures.VolumeOf(
            ManifoldKernel.Evaluate(Solid.Of(body).Subtract(tool), Probe, SolidRetention.None).Value.Outcome.Mesh);

        var volumes = new double[24];
        var failures = 0;
        using var start = new Barrier(7);
        var cutters = Enumerable.Range(0, 6)
            .Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                for (var i = t; i < volumes.Length; i += 6)
                {
                    var cut = booleans.Subtract(body, tool);
                    if (cut.IsFailure)
                    {
                        Interlocked.Increment(ref failures);
                        continue;
                    }

                    volumes[i] = Fixtures.VolumeOf(cut.Value);
                }
            }))
            .ToList();

        var done = false;
        var releaser = new Thread(() =>
        {
            start.SignalAndWait();
            while (!Volatile.Read(ref done))
            {
                booleans.Release(body);
                booleans.Release(tool);
                Thread.Yield();
            }
        });

        cutters.ForEach(thread => thread.Start());
        releaser.Start();
        cutters.ForEach(thread => Check.True(thread.Join(TimeSpan.FromSeconds(60))));
        Volatile.Write(ref done, true);
        Check.True(releaser.Join(TimeSpan.FromSeconds(60)));

        Check.Equal(0, failures);
        foreach (var volume in volumes)
        {
            Check.RelativelyClose(expected, volume, 1e-9);
        }
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
