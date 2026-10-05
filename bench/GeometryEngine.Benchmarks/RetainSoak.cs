using System.Diagnostics;
using System.Runtime.CompilerServices;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Looks for what a unit test cannot see in kept solids: memory that piles up faster than it is
/// let go, and work that stalls while it is.
///
/// A kept solid is freed by a finalizer, which runs when the collector decides to - and the
/// collector sees a small managed object, not the megabytes behind it. Each kept solid declares
/// its size as memory pressure so that it does see them. This run is the check that the
/// declaration works: it prepares, cuts and drops large meshes as fast as it can, never forcing
/// a collection, and watches how many solids are alive at once and how far the process grows.
///
///   pile-up   the most solids alive at any moment, against the one to three a round creates. A
///             figure near the total created means nothing was let go until the end.
///   stall     the slowest round against the median one. A round many times the median is a
///             collection or a backlog of finalizers landing on one caller.
///   leak      memory still held after everything is dropped and collected, at two run
///             lengths. Growth with the length of the run is a leak.
/// </summary>
internal static class RetainSoak
{
    public static int Run()
    {
        var engine = BspGeometryEngine.CreateWithManifold(SolidRetention.Keep);
        var bolus = TestMeshes.Load(engine, "test_smoothed_bolus.stl");
        var stats = engine.Evaluators.GetStatistics(bolus).Value;
        var centre = (stats.BoundsMin + stats.BoundsMax) * 0.5;
        var tool = engine.Generators.GenerateCylinder(centre, 1.5, stats.BoundsSize.Z + 10, 24).Value;

        Console.WriteLine($"Manifold native available: {ManifoldNative.IsAvailable}");
        Console.WriteLine(
            $"{bolus.TriangleCount:N0} triangles a body; a kept solid of that size is about " +
            $"{bolus.TriangleCount * RetainedSolid.BytesPerTriangle / 1048576.0:N0} MB.");
        Console.WriteLine();

        // The same rounds with nothing kept, for what the collector does on the meshes alone.
        var reading = BspGeometryEngine.CreateWithManifold(SolidRetention.None);
        Round(reading, bolus, tool);
        Round(engine, bolus, tool); // warm-up
        // One body the kernel holds, nudged a little further and cut again each round, as a user
        // dragging it would: only the results come and go.
        var held = engine.CreateMesh(bolus.Vertices, bolus.Triangles, bolus.Metadata).Value;
        _ = engine.Booleans.Prepare(held);
        _ = engine.Booleans.Prepare(tool);
        var step = 0;
        void Drag(IGeometryEngine subject) =>
            _ = subject.Booleans.Evaluate(Solid.Of(held).Translate(new Vec3(++step * 0.01, 0, 0)).Subtract(tool)).Value;

        var runs = new (int Rounds, IGeometryEngine Subject, string Label, Action<IGeometryEngine> Round)[]
        {
            (40, reading, "new meshes each round, nothing kept", subject => Round(subject, bolus, tool)),
            (40, engine, "new meshes each round, kept", subject => Round(subject, bolus, tool)),
            (120, engine, "new meshes each round, kept", subject => Round(subject, bolus, tool)),
            (40, engine, "one kept body dragged and cut", Drag),
            (120, engine, "one kept body dragged and cut", Drag),
        };

        Drag(engine);
        var (managedFloor, privateFloor) = Settled();
        var liveFloor = RetainedSolid.Live;

        foreach (var (rounds, subject, label, round) in runs)
        {
            var times = new List<double>(rounds);
            long peakLive = 0, peakPrivate = 0;
            var process = Process.GetCurrentProcess();
            var gen2Before = GC.CollectionCount(2);

            for (var i = 0; i < rounds; i++)
            {
                var watch = Stopwatch.StartNew();
                round(subject);
                times.Add(watch.Elapsed.TotalMilliseconds);

                peakLive = Math.Max(peakLive, RetainedSolid.Live - liveFloor);
                process.Refresh();
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64 - privateFloor);
            }

            var gen2 = GC.CollectionCount(2) - gen2Before;
            var (managedAfter, privateAfter) = Settled();
            times.Sort();

            Console.WriteLine($"{rounds} rounds, {label}");
            Console.WriteLine($"  pile-up   {peakLive,5} solids alive at most; process grew {peakPrivate / 1048576.0,7:N0} MB at most; {gen2} full collections");
            Console.WriteLine($"  stall     median round {times[rounds / 2],7:N1} ms, slowest {times[^1],7:N1} ms");
            Console.WriteLine(
                $"  leak      {RetainedSolid.Live - liveFloor,5} solids alive after; " +
                $"process holds {(privateAfter - privateFloor) / 1048576.0,6:N1} MB more, managed heap {(managedAfter - managedFloor) / 1048576.0,6:N1} MB more");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// A body the kernel has not seen, prepared and cut, and nothing of it referenced on return.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Round(IGeometryEngine engine, IMesh bolus, IMesh tool)
    {
        var body = engine.CreateMesh(bolus.Vertices, bolus.Triangles, bolus.Metadata).Value;
        var cutter = engine.CreateMesh(tool.Vertices, tool.Triangles, tool.Metadata).Value;
        _ = engine.Booleans.Prepare(body);
        _ = engine.Booleans.Subtract(body, cutter).Value;
    }

    private static (long Managed, long Private) Settled()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        var process = Process.GetCurrentProcess();
        process.Refresh();
        return (GC.GetTotalMemory(forceFullCollection: true), process.PrivateMemorySize64);
    }
}
