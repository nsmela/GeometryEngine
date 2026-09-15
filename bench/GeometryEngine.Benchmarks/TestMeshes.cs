using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Locates and loads the Fabolus test meshes committed under <c>bench/files</c>, and
/// pairs each with a generated operand so the boolean operations have something real to
/// chew on. Booleans need two closed solids; the test files are single meshes, so each
/// is cut with a sphere sized and placed to overlap it.
/// </summary>
internal static class TestMeshes
{
    /// <summary>A representative spread: small, medium and a larger binary mesh.</summary>
    public static readonly string[] Representative =
    [
        "eye_bolus.stl",   // ~1.2k triangles, ASCII
        "chin_bolus.stl",  // ~3.2k triangles, ASCII
        "small test.stl",  // ~23k triangles, binary
    ];

    private static readonly Lazy<string> FilesDirectory = new(FindFilesDirectory);

    public static string PathOf(string fileName) => Path.Combine(FilesDirectory.Value, fileName);

    public static IEnumerable<string> All() =>
        Directory.EnumerateFiles(FilesDirectory.Value, "*.stl").OrderBy(path => path);

    public static IMesh Load(IGeometryEngine engine, string fileName)
    {
        var result = engine.IO.Import(PathOf(fileName));
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Could not load {fileName}: {result.Error}");
        }

        return result.Value;
    }

    /// <summary>
    /// A sphere centred on the mesh and about four-tenths of its largest extent across,
    /// which reliably overlaps the interior so union, subtraction and intersection all
    /// produce a non-trivial result.
    /// </summary>
    public static IMesh OverlappingSphere(IGeometryEngine engine, IMesh mesh)
    {
        var stats = engine.Evaluators.GetStatistics(mesh).Value;
        var centre = (stats.BoundsMin + stats.BoundsMax) * 0.5;
        var size = stats.BoundsSize;
        var radius = 0.4 * Math.Max(size.X, Math.Max(size.Y, size.Z));

        return engine.Generators.GenerateSphere(centre, radius, 32).Value;
    }

    private static string FindFilesDirectory()
    {
        // Walk up from the running assembly until a "files" folder with STL in it turns
        // up. That keeps the benchmarks runnable from bin/ without hard-coding a path.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "files");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.stl").Any())
            {
                return candidate;
            }

            var nested = Path.Combine(directory.FullName, "bench", "files");
            if (Directory.Exists(nested) && Directory.EnumerateFiles(nested, "*.stl").Any())
            {
                return nested;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the bench/files directory holding the STL test meshes.");
    }
}
