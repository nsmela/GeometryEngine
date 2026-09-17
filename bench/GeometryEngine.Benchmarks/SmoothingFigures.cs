using BasicResults;
using GeometryEngine.Core.Geometry;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Exports what tools/render/make_smoothing_figures.py needs to draw the smoothing comparison:
/// each mesh smoothed both ways, as binary STL, beside a per-facet signed deviation from the
/// original surface. Splitting it here rather than computing distances in Python keeps the
/// measurement in the engine that will be judged on it.
/// </summary>
internal static class SmoothingFigures
{
    /// <summary>Millimetres of inflate and deflate for the closing.</summary>
    private const double Distance = 2.0;

    /// <summary>λ|μ pairs for the fairing.</summary>
    private const int Iterations = 10;

    public static int Run(string outDirectory)
    {
        var engine = BspGeometryEngine.Create();
        Directory.CreateDirectory(outDirectory);

        var failed = 0;

        foreach (var path in TestMeshes.All())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var loaded = engine.IO.Import(path);
            if (loaded.IsFailure)
            {
                Console.WriteLine($"{name}: {loaded.Error}");
                failed++;
                continue;
            }

            var original = loaded.Value;

            // The closing needs its inflation to span at least one grid cell, and the default
            // grid is scaled to the mesh - so a large model needs the cell size naming rather
            // than left to the engine, or the operation refuses the distance as unresolvable.
            var stats = engine.Evaluators.GetStatistics(original);
            var cellSize = stats.IsSuccess ? Math.Min(Distance / 2, stats.Value.BoundsSize.Length / 200) : 0;

            var laplacian = engine.Modifiers.LaplacianSmooth(original, Iterations, 0.5);
            var closing = engine.Modifiers.OffsetSmooth(original, Distance, 1, cellSize);

            if (laplacian.IsFailure || closing.IsFailure)
            {
                Console.WriteLine(
                    $"{name}: fairing {Code(laplacian)}, closing {Code(closing)}");
                failed++;
                continue;
            }

            using var index = engine.Spatial.BuildIndex(original).Value;

            Export(engine, original, Path.Combine(outDirectory, $"{name}-original.stl"));
            Emit(engine, index, laplacian.Value, outDirectory, name, "fairing");
            Emit(engine, index, closing.Value, outDirectory, name, "closing");

            Console.WriteLine(
                $"{name}: {original.TriangleCount} tris -> fairing {laplacian.Value.TriangleCount}, " +
                $"closing {closing.Value.TriangleCount} (cell {cellSize:F3})");
        }

        return failed == 0 ? 0 : 1;
    }

    private static string Code(Result<IMesh> result) => result.IsSuccess ? "ok" : result.Error.Code;

    private static void Emit(
        IGeometryEngine engine,
        ISpatialIndex original,
        IMesh mesh,
        string outDirectory,
        string name,
        string label)
    {
        Export(engine, mesh, Path.Combine(outDirectory, $"{name}-{label}.stl"));

        // Per vertex first, in one batch - the native field answers a batch in parallel - then
        // averaged onto facets, which is the granularity the renderer colours at.
        var perVertex = original.SignedDistances(mesh.Vertices);

        var lines = new string[mesh.TriangleCount];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = perVertex[mesh.Triangles[t * 3]];
            var b = perVertex[mesh.Triangles[(t * 3) + 1]];
            var c = perVertex[mesh.Triangles[(t * 3) + 2]];
            lines[t] = ((a + b + c) / 3).ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        }

        File.WriteAllLines(Path.Combine(outDirectory, $"{name}-{label}.dev"), lines);
    }

    private static void Export(IGeometryEngine engine, IMesh mesh, string path)
    {
        var encoded = engine.IO.Write(mesh, MeshFileFormat.Stl);
        if (encoded.IsFailure)
        {
            throw new InvalidOperationException($"Could not encode {path}: {encoded.Error}");
        }

        File.WriteAllBytes(path, encoded.Value);
    }
}
