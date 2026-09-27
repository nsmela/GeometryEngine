// Headless generator for the Fabolus main ("reference") side of the smoothing comparison.
//
// Load the STL the way MeshModel.FromFile does, centre it the way BolusStore does on import,
// then run MarchingCubesSmoothing.Smooth at the UI's "standard" preset and write the result.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fabolus.SmoothingCompare.Main;
using static MR.DotNet;

var inputs = new List<string>();
string outDir = null;
string centredDir = null;
var iterations = 1;
var deflate = 1.0f;
var inflate = 0.1f;
var cellSize = 1.0f;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--centred-out": centredDir = args[++i]; break;
        case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--deflate": deflate = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--inflate": inflate = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--cell-size": cellSize = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default: inputs.Add(args[i]); break;
    }
}

if (outDir is null || inputs.Count == 0)
{
    Console.Error.WriteLine("usage: main-harness --out <dir> [--centred-out <dir>] [--iterations n] " +
                            "[--deflate mm] [--inflate mm] [--cell-size mm] <input.stl>...");
    return 2;
}

Directory.CreateDirectory(outDir);
if (centredDir is not null) Directory.CreateDirectory(centredDir);

var records = new List<Record>();
var failed = 0;

foreach (var input in inputs)
{
    var name = Path.GetFileNameWithoutExtension(input);
    var total = Stopwatch.StartNew();
    try
    {
        var loaded = MeshLoad.FromAnySupportedFormat(input);

        // new MeshModel(Mesh): DMesh3 from the loaded mesh, _mesh the loaded mesh itself.
        var dmesh = loaded.ToDMesh();
        var mr = loaded;

        // BolusStore centres twice; the second pass is a no-op on an already-centred box.
        (dmesh, mr) = FabolusMain.OrientationCentre(dmesh, mr);
        (dmesh, mr) = FabolusMain.OrientationCentre(dmesh, mr);

        // Bolus.Mesh -> implicit operator Mesh(MeshModel) => model.Mesh.ToMesh()
        var centred = dmesh.ToMesh();
        var inputTriangles = centred.ValidFaces.Count();

        if (centredDir is not null)
        {
            MeshSave.ToAnySupportedFormat(centred, Path.Combine(centredDir, name + ".stl"));
        }

        var smoothed = FabolusMain.Smooth(centred, deflate, inflate, iterations, cellSize,
            out var offsetCycleMs, out var inflateMs, out var resizeMs, out var inflationVoxel);

        total.Stop();

        MeshSave.ToAnySupportedFormat(smoothed, Path.Combine(outDir, name + ".stl"));

        records.Add(new Record
        {
            Case = name,
            Ok = true,
            InputTriangles = inputTriangles,
            OutputTriangles = smoothed.ValidFaces.Count(),
            OutputVertices = smoothed.Points.Count,
            InflationVoxelSizeMm = Math.Round(inflationVoxel, 4),
            OffsetCycleMs = Math.Round(offsetCycleMs, 1),
            InflateMs = Math.Round(inflateMs, 1),
            ResizeMs = Math.Round(resizeMs, 1),
            TotalMs = Math.Round(total.Elapsed.TotalMilliseconds, 1),
        });

        Console.Error.WriteLine($"main {name}: {smoothed.ValidFaces.Count()} tris in {total.Elapsed.TotalSeconds:F1}s");
    }
    catch (Exception failure)
    {
        records.Add(new Record { Case = name, Ok = false, Error = $"{failure.GetType().Name}: {failure.Message}" });
        failed++;
        Console.Error.WriteLine($"main {name}: FAILED {failure.GetType().Name}: {failure.Message}");
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    engine = "Fabolus main (MeshLib)",
    settings = new { iterations, deflate_distance = deflate, inflate_distance = inflate, cell_size = cellSize },
    cases = records,
}, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));

return failed == 0 ? 0 : 1;

internal sealed class Record
{
    public string Case { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; }
    public int? InputTriangles { get; set; }
    public int? OutputTriangles { get; set; }
    public int? OutputVertices { get; set; }
    public double? InflationVoxelSizeMm { get; set; }
    public double? OffsetCycleMs { get; set; }
    public double? InflateMs { get; set; }
    public double? ResizeMs { get; set; }
    public double? TotalMs { get; set; }
}
