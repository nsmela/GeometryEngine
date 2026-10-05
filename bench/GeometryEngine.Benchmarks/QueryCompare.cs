using System.Collections.Immutable;
using System.Diagnostics;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Times one description against the same steps asked for a call at a time.
///
/// The shape is a mould with every kind of step in it: a block less the bolus and its air
/// channels, joined to four locating lugs, clipped to the build volume. The batch calls cannot
/// take it whole - they are one operation each - so the fairest "before" is the batches chained,
/// and the plainest is one call per step. Both hand every intermediate result back as a mesh and
/// read it in again; the description does neither.
///
/// Each figure is the median of several runs after a warm-up, and the volume is printed beside
/// it so a faster line that built a different solid cannot pass unnoticed.
/// </summary>
internal static class QueryCompare
{
    private const int Runs = 5;

    public static int Run()
    {
        var engine = BspGeometryEngine.Create();
        Console.WriteLine($"Manifold native available: {ManifoldNative.IsAvailable}");
        Console.WriteLine();

        foreach (var file in new[] { "chin_bolus.stl", "small test.stl", "test_smoothed_bolus.stl" })
        {
            var bolus = TestMeshes.Load(engine, file);
            var stats = engine.Evaluators.GetStatistics(bolus).Value;
            var (min, max) = (stats.BoundsMin, stats.BoundsMax);
            var centre = (min + max) * 0.5;
            var size = stats.BoundsSize;

            Console.WriteLine($"{file}: {bolus.TriangleCount:N0} triangles");

            var block = engine.Generators.GenerateBox(min - new Vec3(5, 5, 5), max + new Vec3(5, 5, 5)).Value;
            var channels = Enumerable.Range(0, 8)
                .Select(i =>
                {
                    var angle = i * Math.PI / 4;
                    var foot = centre + new Vec3(Math.Cos(angle) * size.X * 0.25, Math.Sin(angle) * size.Y * 0.25, 0);
                    return engine.Generators.GenerateCylinder(foot, 1.5, max.Z - centre.Z + 10, 24).Value;
                })
                .ToImmutableArray();

            // A lug at each corner of the block, half inside it.
            var lugs = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
                .Select(corner =>
                {
                    var at = new Vec3(
                        corner.Item1 < 0 ? min.X - 5 : max.X + 5,
                        corner.Item2 < 0 ? min.Y - 5 : max.Y + 5,
                        centre.Z);
                    return engine.Generators.GenerateSphere(at, 4, 24).Value;
                })
                .ToImmutableArray();

            // The build volume: everything but the top two units of the block.
            var envelope = engine.Generators.GenerateBox(
                min - new Vec3(20, 20, 20), new Vec3(max.X + 20, max.Y + 20, max.Z + 3)).Value;

            ImmutableArray<IMesh> cavities = [bolus, .. channels];

            Report(engine, "one call per step", () =>
            {
                var cut = cavities.Aggregate(block, (current, tool) => engine.Booleans.Subtract(current, tool).Value);
                var lugged = lugs.Aggregate(cut, (current, lug) => engine.Booleans.Union(current, lug).Value);
                return engine.Booleans.Intersect(lugged, envelope).Value;
            });

            Report(engine, "batches, chained", () =>
            {
                var cut = engine.Booleans.Subtract(block, cavities).Value;
                var lugged = engine.Booleans.Union([cut, .. lugs]).Value;
                return engine.Booleans.Intersect(lugged, envelope).Value;
            });

            Report(engine, "one description", () =>
            {
                var cut = cavities.Aggregate(Solid.Of(block), (solid, tool) => solid.Subtract(tool));
                var lugged = lugs.Aggregate(cut, (solid, lug) => solid.Union(lug));
                return engine.Booleans.Evaluate(lugged.Intersect(envelope)).Value;
            });

            Console.WriteLine();
        }

        return 0;
    }

    private static void Report(IGeometryEngine engine, string label, Func<IMesh> operation)
    {
        var result = operation(); // warm-up, and the figures the result line reports
        var times = new List<double>(Runs);
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            _ = operation();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var volume = engine.Evaluators.GetStatistics(result).Value.Volume;
        Console.WriteLine(
            $"  {label,-20} {times[Runs / 2],9:N1} ms   {result.TriangleCount,9:N0} tris   volume {volume,12:N3}   {result.Metadata.CreatedBy}");
    }
}
