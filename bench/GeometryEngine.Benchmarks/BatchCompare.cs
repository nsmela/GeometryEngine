using System.Collections.Immutable;
using System.Diagnostics;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Times the batch booleans and the plane split against what a caller had to do before they
/// existed: a boolean per tool, and a cut as an intersection and a subtraction against a box.
///
/// The shapes stand in for Fabolus's two heaviest uses. A mould is a block with the bolus and
/// its air channels subtracted from it; a label is a run of small prisms joined onto a bolus.
/// Each is measured as the median of several runs after a warm-up, so a one-off JIT or page
/// fault does not land in the figure.
/// </summary>
internal static class BatchCompare
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

            // The mould: a block around the bolus, less the bolus and eight vertical channels.
            var block = engine.Generators.GenerateBox(min - new Vec3(5, 5, 5), max + new Vec3(5, 5, 5)).Value;
            var channels = Enumerable.Range(0, 8)
                .Select(i =>
                {
                    var angle = i * Math.PI / 4;
                    var foot = centre + new Vec3(Math.Cos(angle) * size.X * 0.25, Math.Sin(angle) * size.Y * 0.25, 0);
                    return engine.Generators.GenerateCylinder(foot, 1.5, max.Z - centre.Z + 10, 24).Value;
                })
                .ToImmutableArray();
            ImmutableArray<IMesh> cavities = [bolus, .. channels];

            Report("mould, one subtraction each", () => Fold(engine, block, cavities));
            Report("mould, one batch subtraction", () => engine.Booleans.Subtract(block, cavities).Value);

            // The label: twelve small blocks along the top of the bolus, joined onto it.
            var prisms = Enumerable.Range(0, 12)
                .Select(i =>
                {
                    var x = min.X + (size.X * (i + 0.5) / 12);
                    return engine.Generators.GenerateBox(new Vec3(x - 1, centre.Y - 2, max.Z - 3), new Vec3(x + 1, centre.Y + 2, max.Z + 1)).Value;
                })
                .ToImmutableArray();

            Report("label, one union each", () => prisms.Aggregate(bolus, (current, prism) => engine.Booleans.Union(current, prism).Value));
            Report("label, merged first, one union", () =>
                engine.Booleans.Union(bolus, prisms.Skip(1).Aggregate(prisms[0], (merged, prism) => engine.Booleans.Union(merged, prism).Value)).Value);
            Report("label, one batch union", () => engine.Booleans.Union([bolus, .. prisms]).Value);

            // The cut: across the middle, as a box intersection and subtraction, and as a split.
            var plane = Plane.FromNormalAndPoint(Direction.From(new Vec3(0.3, 0.2, 1)).Value, centre);
            var reach = size.Length + 1;
            var halfSpace = engine.Transforms.Translate(
                engine.Generators.GenerateBox(new Vec3(-reach, -reach, 0), new Vec3(reach, reach, reach * 2)).Value,
                centre).Value;
            var tilted = RotateOnto(engine, halfSpace, centre, plane.Normal);

            Report("cut, intersect + subtract", () =>
            {
                _ = engine.Booleans.Intersect(bolus, tilted).Value;
                return engine.Booleans.Subtract(bolus, tilted).Value;
            });
            Report("cut, split", () => engine.Booleans.Split(bolus, plane).Value.Back);

            Console.WriteLine();
        }

        return 0;
    }

    private static IMesh Fold(IGeometryEngine engine, IMesh subject, ImmutableArray<IMesh> tools) =>
        tools.Aggregate(subject, (current, tool) => engine.Booleans.Subtract(current, tool).Value);

    /// <summary>Turns a box standing on the XY plane at <paramref name="pivot"/> so it stands on the plane's normal instead.</summary>
    private static IMesh RotateOnto(IGeometryEngine engine, IMesh box, Vec3 pivot, Direction normal)
    {
        var axis = Direction.From(Vec3.UnitZ.Cross(normal.Vector));
        if (!axis.HasValue)
        {
            return box;
        }

        var angle = Math.Acos(Math.Clamp(Vec3.UnitZ.Dot(normal.Vector), -1, 1));
        var atOrigin = engine.Transforms.Translate(box, -pivot).Value;
        var turned = engine.Transforms.Rotate(atOrigin, axis.Value, angle).Value;
        return engine.Transforms.Translate(turned, pivot).Value;
    }

    private static void Report(string label, Func<IMesh> operation)
    {
        var result = operation(); // warm-up, and the figure the result line reports
        var times = new List<double>(Runs);
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            _ = operation();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        Console.WriteLine($"  {label,-32} {times[Runs / 2],9:N1} ms   {result.TriangleCount,9:N0} tris   {result.Metadata.CreatedBy}");
    }
}
