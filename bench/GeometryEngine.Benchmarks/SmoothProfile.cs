using System.Diagnostics;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Internal.Native;
using GeometryEngine.Internal.Smoothing;
using GeometryEngine.Internal.Spatial;
using GeometryEngine.Modifiers;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Times an offset-smooth - a closing on a sampled distance grid - with the grid filled both
/// ways, and then phase by phase.
///
///   every node exactly   each grid node asked for its exact distance to the mesh, as it was.
///   near the surface     each node asked only whether the surface is within the closing's
///                        reach; a node beyond it takes the side of the node before it.
///
/// Both must produce the same mesh, vertex for vertex, and the run says whether they did.
///
/// The phases are there because the two that matter scale differently with cores. Sampling runs
/// one grid plane to a task, and the level-set meshing is Manifold's, on its own threads. On one
/// core sampling every node was two thirds or more of the operation; what share is left to gain
/// on a machine with several is what this run is for.
///
/// Every timed call is on a mesh nothing has indexed, so each pays for its own index.
/// </summary>
internal static class SmoothProfile
{
    private const int Runs = 5;
    private const double Distance = 1.5;

    public static int Run()
    {
        var engine = BspGeometryEngine.CreateWithManifold(SolidRetention.None);
        Console.WriteLine($"Manifold native available: {ManifoldNative.IsAvailable}");
        Console.WriteLine($"logical processors: {Environment.ProcessorCount}");
        Console.WriteLine($"closing distance {Distance}, one iteration, cell left to the engine");

        foreach (var file in new[] { "ear_bolus.stl", "small test.stl", "test_smoothed_bolus.stl" })
        {
            var mesh = TestMeshes.Load(engine, file);
            IMesh Unseen() => engine.CreateMesh(mesh.Vertices, mesh.Triangles, mesh.Metadata).Value;

            var planned = OffsetSmoothHandler.GridFor(mesh, Distance, 0);
            Console.WriteLine();
            if (planned.IsFailure)
            {
                Console.WriteLine($"{file}: {mesh.TriangleCount:N0} triangles - not closed at this distance: {planned.Error}");
                continue;
            }

            var (min, max, cell, reach) = planned.Value;
            var tree = new MeshBvh(mesh);
            var exact = SignedDistanceGrid.Sample(tree.SignedDistance, min, max, cell);
            var within = 0;
            foreach (var value in exact.Values)
            {
                within += Math.Abs(value) < reach ? 1 : 0;
            }

            Console.WriteLine(
                $"{file}: {mesh.TriangleCount:N0} triangles, cell {cell:N3}, {exact.CellCount:N0} grid nodes, " +
                $"{within:N0} within reach ({reach:N2}) of the surface; " +
                $"sampled near the surface: {(OffsetSmoothHandler.CanSampleNearSurface(mesh) ? "yes" : "no, the mesh is not closed and consistently wound")}");

            var request = (IMesh input) => new OffsetSmoothRequest(input, Distance, 1, 0);
            var everyNode = new OffsetSmoothHandler(GridSampling.EveryNode);
            var nearSurface = new OffsetSmoothHandler(GridSampling.NearSurface);

            var before = everyNode.Handle(request(Unseen())).Value;
            var after = nearSurface.Handle(request(Unseen())).Value;
            var same = before.Vertices.AsSpan().SequenceEqual(after.Vertices.AsSpan())
                && before.Triangles.AsSpan().SequenceEqual(after.Triangles.AsSpan());

            Report("the whole closing / every node exactly", () =>
            {
                var input = Unseen();
                return () => everyNode.Handle(request(input));
            });
            Report("the whole closing / near the surface", () =>
            {
                var input = Unseen();
                return () => nearSurface.Handle(request(input));
            });
            Console.WriteLine(
                $"  the same mesh either way: {(same ? "yes" : "NO")}   " +
                $"({after.TriangleCount:N0} triangles, volume {engine.Evaluators.GetStatistics(after).Value.Volume:N3})");

            Report("  phase: build the index", () => () => _ = new MeshBvh(mesh));
            Report("  phase: sample every node exactly", () => () => SignedDistanceGrid.Sample(tree.SignedDistance, min, max, cell));
            Report("  phase: sample near the surface", () => () => SignedDistanceGrid.SampleNear(tree, min, max, cell, reach));
            Report("  phase: inflate and deflate on the grid", () =>
            {
                var grid = SignedDistanceGrid.SampleNear(tree, min, max, cell, reach);
                return () =>
                {
                    grid.Shift(Distance);
                    grid.Reinitialise();
                    grid.Shift(-Distance);
                    grid.Reinitialise();
                };
            });
            Report("  phase: mesh the level set", () =>
            {
                var grid = SignedDistanceGrid.SampleNear(tree, min, max, cell, reach);
                grid.Shift(Distance);
                grid.Reinitialise();
                grid.Shift(-Distance);
                grid.Reinitialise();
                return () => ManifoldKernel.LevelSet(grid.Sample, min, max, cell, 0, MeshMetadata.Named("closed"));
            });
        }

        return 0;
    }

    /// <param name="prepare">Builds one run's inputs, untimed, and returns the work to time.</param>
    private static void Report(string label, Func<Action> prepare)
    {
        prepare()(); // warm-up

        var times = new List<double>(Runs);
        for (var i = 0; i < Runs; i++)
        {
            var work = prepare();
            var watch = Stopwatch.StartNew();
            work();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        Console.WriteLine($"  {label,-46} {times[Runs / 2],9:N1} ms");
    }
}
