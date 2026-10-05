using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Times the same work on an engine that keeps native solids with their meshes and on one that
/// does not.
///
/// Four shapes of work. A single description cannot cover the first two, because the caller
/// wants each result in hand before deciding the next step; the last two are how an interactive
/// caller meets a mesh for the first time and comes back to a description:
///
///   one body, eight cuts   the same bolus cut by a different channel each time, as when a user
///                          moves a channel and looks again. Keeping reads the bolus in once.
///   a chain, step by step  a block less the bolus and eight channels, one call per step, each
///                          result fed to the next. Keeping never reads a result back in.
///   the first cut          one cut of a mesh the kernel has not seen, and the same cut after
///                          IBooleans.Prepare has read the mesh in ahead of it.
///   a description, redone  the same mould as one description, evaluated a second time with one
///                          channel replaced. Keeping reads in only the channel that changed.
///
/// Every run starts from meshes the kernel has not seen, so the first read is always paid.
/// The last column is how many solids the engine was keeping when the run finished.
/// </summary>
internal static class RetainCompare
{
    private const int Runs = 5;

    public static int Run()
    {
        var reading = BspGeometryEngine.CreateWithManifold(SolidRetention.None);
        var keeping = BspGeometryEngine.CreateWithManifold(SolidRetention.Keep);
        Console.WriteLine($"Manifold native available: {ManifoldNative.IsAvailable}");
        Console.WriteLine();

        foreach (var file in new[] { "chin_bolus.stl", "small test.stl", "test_smoothed_bolus.stl" })
        {
            var bolus = TestMeshes.Load(reading, file);
            var stats = reading.Evaluators.GetStatistics(bolus).Value;
            var (min, max) = (stats.BoundsMin, stats.BoundsMax);
            var centre = (min + max) * 0.5;
            var size = stats.BoundsSize;

            Console.WriteLine($"{file}: {bolus.TriangleCount:N0} triangles");

            var block = reading.Generators.GenerateBox(min - new Vec3(5, 5, 5), max + new Vec3(5, 5, 5)).Value;
            var channels = Enumerable.Range(0, 8)
                .Select(i =>
                {
                    var angle = i * Math.PI / 4;
                    var foot = centre + new Vec3(Math.Cos(angle) * size.X * 0.25, Math.Sin(angle) * size.Y * 0.25, 0);
                    return reading.Generators.GenerateCylinder(foot, 1.5, max.Z - centre.Z + 10, 24).Value;
                })
                .ToImmutableArray();

            foreach (var (label, engine) in new[] { ("read in each time", reading), ("kept", keeping) })
            {
                Report(engine, $"one body, eight cuts  / {label}", () =>
                {
                    var body = Unseen(engine, bolus);
                    var tools = channels.Select(channel => Unseen(engine, channel)).ToArray();
                    return () =>
                    {
                        // A loop, not Select(...).Last(): LINQ would run the last cut alone.
                        IMesh last = body;
                        foreach (var tool in tools)
                        {
                            last = engine.Booleans.Subtract(body, tool).Value;
                        }

                        return last;
                    };
                });
            }

            foreach (var (label, engine) in new[] { ("read in each time", reading), ("kept", keeping) })
            {
                Report(engine, $"a chain, step by step / {label}", () =>
                {
                    var start = Unseen(engine, block);
                    var tools = channels.Prepend(bolus).Select(tool => Unseen(engine, tool)).ToArray();
                    return () => tools.Aggregate(start, (current, tool) => engine.Booleans.Subtract(current, tool).Value);
                });
            }

            // The first cut of a mesh, as it is and with the mesh prepared beforehand - untimed,
            // as it would be on another thread - and what preparing costs wherever it is paid.
            Report(keeping, "the first cut         / as it comes", () =>
            {
                var (body, tool) = (Unseen(keeping, bolus), Unseen(keeping, channels[0]));
                return () => keeping.Booleans.Subtract(body, tool).Value;
            });

            Report(keeping, "the first cut         / prepared", () =>
            {
                var (body, tool) = (Unseen(keeping, bolus), Unseen(keeping, channels[0]));
                _ = keeping.Booleans.Prepare(body);
                _ = keeping.Booleans.Prepare(tool);
                return () => keeping.Booleans.Subtract(body, tool).Value;
            });

            Report(keeping, "preparing the body    / alone", () =>
            {
                var body = Unseen(keeping, bolus);
                return () =>
                {
                    _ = keeping.Booleans.Prepare(body);
                    return body;
                };
            });

            foreach (var (label, engine) in new[] { ("read in each time", reading), ("kept", keeping) })
            {
                Report(engine, $"a description, redone / {label}", () =>
                {
                    var start = Unseen(engine, block);
                    var tools = channels.Prepend(bolus).Select(tool => Unseen(engine, tool)).ToArray();
                    IMesh Mould(IMesh[] with) => engine.Booleans.Evaluate(
                        with.Aggregate(Solid.Of(start), (solid, tool) => solid.Subtract(tool))).Value;

                    // Evaluated once, untimed; then one channel is replaced and it is evaluated again.
                    var first = Mould(tools);
                    var moved = tools.ToArray();
                    moved[^1] = Unseen(engine, tools[^1]);
                    return () =>
                    {
                        GC.KeepAlive(first);
                        return Mould(moved);
                    };
                });
            }

            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>The same geometry as a mesh the kernel has kept nothing for.</summary>
    private static IMesh Unseen(IGeometryEngine engine, IMesh mesh) =>
        engine.CreateMesh(mesh.Vertices, mesh.Triangles, mesh.Metadata).Value;

    /// <param name="prepare">Builds a run's meshes, untimed, and returns the work to time on them.</param>
    private static void Report(IGeometryEngine engine, string label, Func<Func<IMesh>> prepare)
    {
        _ = Once(engine, prepare); // warm-up

        var runs = new List<(double Milliseconds, long Kept, double Volume)>(Runs);
        for (var i = 0; i < Runs; i++)
        {
            runs.Add(Once(engine, prepare));
        }

        var median = runs.Select(run => run.Milliseconds).Order().ElementAt(Runs / 2);
        Console.WriteLine(
            $"  {label,-42} {median,9:N1} ms   volume {runs[^1].Volume,12:N3}   {runs.Max(run => run.Kept),3} solids kept");
    }

    /// <summary>
    /// One run, out of line so that nothing of it is still referenced when it returns: the next
    /// run then starts from a kernel holding nothing, and the count of what this one kept is its
    /// own.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (double Milliseconds, long Kept, double Volume) Once(IGeometryEngine engine, Func<Func<IMesh>> prepare)
    {
        Settle();
        var before = RetainedSolid.Live;

        var work = prepare();
        var watch = Stopwatch.StartNew();
        var result = work();
        var elapsed = watch.Elapsed.TotalMilliseconds;

        // Read while the run's meshes are all still referenced, which is as many as it ever keeps
        // unless the collector has already taken an intermediate result.
        var kept = RetainedSolid.Live - before;
        GC.KeepAlive(work);
        return (elapsed, kept, engine.Evaluators.GetStatistics(result).Value.Volume);
    }

    private static void Settle()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
