using System.Diagnostics;
using BasicResults;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;
using GeometryEngine.Internal.Spatial;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Compares the two ways this repository can reduce a triangle count: the quadric decimator in
/// Internal/Decimation, and Manifold's own <c>Simplify</c>.
///
/// They are not interchangeable, and the report is arranged to show why rather than to declare a
/// winner. The decimator takes a target triangle count and solves a quadric for each collapsed
/// vertex, so it invents new positions and will hit any count asked of it, with no bound on how
/// far the surface moves. Simplify takes a tolerance, keeps a subset of the original vertices,
/// and guarantees nothing moves further than that tolerance - but it cannot be asked for a count,
/// and on a shape with no redundant detail it will barely reduce at all.
///
/// So the honest comparison is at matched output size: run the decimator to a target, run
/// Simplify across a tolerance sweep, and read off the deviation each incurred for the triangle
/// count it actually produced.
/// </summary>
internal static class DecimateCompare
{
    /// <summary>Targets as a fraction of the input triangle count.</summary>
    private static readonly double[] Fractions = [0.75, 0.5, 0.25];

    /// <summary>Tolerances as a fraction of the bounding-box diagonal.</summary>
    private static readonly double[] ToleranceFractions = [0.0001, 0.0005, 0.001, 0.005, 0.01, 0.05];

    public static int Run()
    {
        var engine = BspGeometryEngine.Create();

        Console.WriteLine($"Manifold native available: {ManifoldNative.IsAvailable}");
        if (!ManifoldNative.IsAvailable)
        {
            Console.WriteLine("Simplify needs the native library; nothing to compare.");
            return 1;
        }

        Console.WriteLine();

        foreach (var path in TestMeshes.All())
        {
            var name = Path.GetFileName(path);
            IMesh mesh;
            try
            {
                mesh = TestMeshes.Load(engine, name);
            }
            catch (InvalidOperationException exception)
            {
                Console.WriteLine($"## {name}: could not load - {exception.Message}");
                Console.WriteLine();
                continue;
            }

            Report(engine, name, mesh);
        }

        return 0;
    }

    private static void Report(IGeometryEngine engine, string name, IMesh mesh)
    {
        var stats = engine.Evaluators.GetStatistics(mesh);
        if (stats.IsFailure)
        {
            Console.WriteLine($"## {name}: no statistics - {stats.Error}");
            Console.WriteLine();
            return;
        }

        var diagonal = stats.Value.BoundsSize.Length;
        var topology = engine.Evaluators.ValidateTopology(mesh);
        var closed = topology.IsSuccess && topology.Value.IsClosed;

        Console.WriteLine($"## {name} - {mesh.TriangleCount} tris, diagonal {diagonal:F2}, closed {closed}");
        Console.WriteLine();
        Console.WriteLine("| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---|---|");

        // The original's BVH is reused across every row; building it once is the whole point of
        // measuring deviation this way rather than per-comparison.
        var original = new MeshBvh(mesh);
        var originalVolume = stats.Value.Volume;

        foreach (var fraction in Fractions)
        {
            var target = Math.Max(4, (int)(mesh.TriangleCount * fraction));
            Measure(
                engine,
                "quadric",
                $"{fraction:P0} tris",
                mesh,
                original,
                originalVolume,
                diagonal,
                () => Outcome.From(engine.Modifiers.Decimate(mesh, target)));
        }

        foreach (var toleranceFraction in ToleranceFractions)
        {
            var tolerance = diagonal * toleranceFraction;
            Measure(
                engine,
                "simplify",
                $"tol {toleranceFraction:P2}",
                mesh,
                original,
                originalVolume,
                diagonal,
                () => Outcome.From(ManifoldKernel.Simplify(mesh, tolerance, mesh.Metadata)));
        }

        Console.WriteLine();
    }

    /// <summary>
    /// A reduced mesh plus whether Manifold had to weld the input before it could work on it.
    /// That distinction decides how to read the deviation column: a welded input was altered
    /// before simplification even began, so the error measured is not all the simplifier's.
    /// </summary>
    private readonly record struct Outcome(IMesh Mesh, bool Welded)
    {
        public static Result<Outcome> From(Result<IMesh> result) =>
            result.IsSuccess
                ? Result.Success(new Outcome(result.Value, false))
                : Result.Failure<Outcome>(result.Error);

        public static Result<Outcome> From(Result<ManifoldOutcome> result) =>
            result.IsSuccess
                ? Result.Success(new Outcome(
                    result.Value.Mesh,
                    result.Value.Provenance == ManifoldProvenance.NativeAfterMergingOperands))
                : Result.Failure<Outcome>(result.Error);
    }

    private static void Measure(
        IGeometryEngine engine,
        string method,
        string ask,
        IMesh source,
        MeshBvh original,
        double originalVolume,
        double diagonal,
        Func<Result<Outcome>> operation)
    {
        // One warm run before timing, so the first row of a file does not absorb JIT and
        // first-touch costs that the rest avoid.
        operation();

        var stopwatch = Stopwatch.StartNew();
        var result = operation();
        stopwatch.Stop();

        if (result.IsFailure)
        {
            Console.WriteLine($"| {method} | {ask} | - | - | - | - | - | - | {result.Error.Code} | - |");
            return;
        }

        var reduced = result.Value.Mesh;
        var deviation = SymmetricDeviation(original, source, reduced);
        var stats = engine.Evaluators.GetStatistics(reduced);
        var topology = engine.Evaluators.ValidateTopology(reduced);

        var volumeChange = stats.IsSuccess && Math.Abs(originalVolume) > double.Epsilon
            ? (stats.Value.Volume - originalVolume) / originalVolume
            : double.NaN;

        var percentOfInput = (double)reduced.TriangleCount / source.TriangleCount;
        var closed = topology.IsSuccess ? topology.Value.IsClosed.ToString() : topology.Error.Code;

        Console.WriteLine(
            $"| {method} | {ask} | {reduced.TriangleCount} | {percentOfInput:P1} | " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F1} | {deviation:F4} | {deviation / diagonal:P3} | " +
            $"{volumeChange:P2} | {closed} | {(result.Value.Welded ? "yes" : "no")} |");
    }

    /// <summary>
    /// Approximates the two-sided Hausdorff distance by measuring each mesh's vertices against
    /// the other's surface, and taking the worse of the two.
    ///
    /// One direction alone is not enough and flatters the result. Measuring only the reduced
    /// mesh's vertices misses detail that was deleted outright - the apex of a removed bump is
    /// no longer a vertex, so nothing samples where the error actually is. Measuring only the
    /// original's vertices misses a vertex the decimator moved to a position the original
    /// surface never occupied. Vertices are not a dense sample of either surface, so this
    /// underestimates the true Hausdorff distance in both directions; it is a floor, and the
    /// comparison between rows is what it is for.
    /// </summary>
    private static double SymmetricDeviation(MeshBvh original, IMesh source, IMesh reduced)
    {
        var worst = 0.0;

        foreach (var vertex in reduced.Vertices)
        {
            worst = Math.Max(worst, Math.Abs(original.SignedDistance(vertex)));
        }

        var reducedBvh = new MeshBvh(reduced);
        foreach (var vertex in source.Vertices)
        {
            worst = Math.Max(worst, Math.Abs(reducedBvh.SignedDistance(vertex)));
        }

        return worst;
    }
}
