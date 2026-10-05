using GeometryEngine.Booleans;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Tests.Booleans;

/// <summary>
/// Preparing a mesh reads it into the kernel before anything is done to it, so the first
/// operation costs what the second does. Each test builds its own meshes, for the reason given
/// on <see cref="RetainedSolidTests"/>.
/// </summary>
[Suite("Booleans / preparing a mesh")]
public sealed class PrepareTests
{
    private static readonly MeshMetadata Probe = MeshMetadata.Named("probe");

    private static IBooleans Keeping => BspGeometryEngine.CreateWithManifold(SolidRetention.Keep).Booleans;

    private static IMesh Body() => Fixtures.Box(new Vec3(0, 0, 0), new Vec3(4, 4, 4));

    private static IMesh Cavity() => Fixtures.Box(new Vec3(1, 1, 2), new Vec3(3, 3, 6));

    [Fact]
    public void A_prepared_mesh_is_not_read_in_by_the_first_operation_on_it()
    {
        var (body, cavity) = (Body(), Cavity());

        Check.True(Keeping.Prepare(body).IsSuccess);
        var cut = ManifoldKernel.Evaluate(Solid.Of(body).Subtract(cavity), Probe, SolidRetention.Keep).Value;

        // Only the cavity is new to the kernel.
        Check.Equal(1, cut.MeshesImported);
        Check.RelativelyClose(56, Fixtures.VolumeOf(cut.Outcome.Mesh), 1e-9);
    }

    [Fact]
    public void Preparing_a_mesh_twice_reads_it_in_once()
    {
        var body = Body();
        var before = SettledLive();

        Check.True(Keeping.Prepare(body).IsSuccess);
        Check.True(Keeping.Prepare(body).IsSuccess);

        Check.Equal(before + 1, RetainedSolid.Live);
        GC.KeepAlive(body);
    }

    [Fact]
    public void A_mesh_with_no_geometry_cannot_be_prepared()
    {
        var result = Keeping.Prepare(ImmutableMesh.Empty);

        Check.True(result.IsFailure);
        Check.Equal("Mesh.EmptyOperand", result.Error.Code);
    }

    [Fact]
    public void A_mesh_the_kernel_will_not_take_is_reported_and_still_usable()
    {
        var box = Body();
        var lidless = Fixtures.Engine.CreateMesh(box.Vertices, box.Triangles[..^6], MeshMetadata.Named("lidless")).Value;
        var before = SettledLive();

        var prepared = Keeping.Prepare(lidless);

        Check.True(prepared.IsFailure);
        Check.Equal("Manifold.InvalidMesh", prepared.Error.Code);
        Check.True(prepared.Error.Description.Contains("lidless", StringComparison.Ordinal));
        Check.Equal(before, RetainedSolid.Live);

        // The operation itself is answered as it always was, by the managed kernel.
        var cut = Keeping.Subtract(lidless, Cavity());
        Check.True(cut.IsSuccess);
        Check.Equal(ManifoldBooleanOperations.FallbackProducer, cut.Value.Metadata.CreatedBy);
        GC.KeepAlive(lidless);
    }

    [Fact]
    public void An_engine_that_keeps_nothing_has_nothing_to_prepare()
    {
        var body = Body();
        var before = SettledLive();

        var result = BspGeometryEngine.CreateWithManifold(SolidRetention.None).Booleans.Prepare(body);

        Check.True(result.IsSuccess);
        Check.Equal(before, RetainedSolid.Live);
        GC.KeepAlive(body);
    }

    [Fact]
    public void The_managed_engine_accepts_the_hint_and_does_nothing()
    {
        var managed = BspGeometryEngine.CreateManagedBsp().Booleans;

        Check.True(managed.Prepare(Body()).IsSuccess);
        Check.True(managed.Prepare(ImmutableMesh.Empty).IsFailure);
    }

    [Fact]
    public void A_mesh_can_be_prepared_on_one_thread_while_another_cuts_it()
    {
        var booleans = Keeping;
        var body = Fixtures.Sphere(Vec3.Zero, 10);
        var tool = Fixtures.Sphere(new Vec3(8, 0, 0), 4, 24);
        var expected = Fixtures.VolumeOf(
            ManifoldKernel.Evaluate(Solid.Of(body).Subtract(tool), Probe, SolidRetention.None).Value.Outcome.Mesh);

        var prepared = new Result[4];
        var volumes = new double[4];
        using var start = new Barrier(8);
        var threads = Enumerable.Range(0, 8)
            .Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                if (t % 2 == 0)
                {
                    prepared[t / 2] = booleans.Prepare(body);
                }
                else
                {
                    volumes[t / 2] = Fixtures.VolumeOf(booleans.Subtract(body, tool).Value);
                }
            }))
            .ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Check.True(prepared.All(result => result.IsSuccess));
        foreach (var volume in volumes)
        {
            Check.RelativelyClose(expected, volume, 1e-9);
        }

        Check.Equal(
            0, ManifoldKernel.Evaluate(Solid.Of(body).Subtract(tool), Probe, SolidRetention.Keep).Value.MeshesImported);
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
