// Headless generator for the GeometryEngine ("candidate") side of the smoothing comparison.
//
// It reproduces, without a UI, exactly what Fabolus on feat/geometry-engine does to a mesh
// between "File > Open" and "Smooth" at the default preset:
//
//   ImportMesh.Execute       -> IO.Import, then a TranslateCommand by -(bounds centre)
//   SmoothSettings.Apply     -> Modifiers.DoubleOffset(Intensity, Iterations, Resolution)
//                               Modifiers.Offset(Inflation, Resolution)
//                               Modifiers.Decimate(inputTriangles * max(RemeshRatio, 1))
//
// Component separation, which ImportMesh also does, is deliberately skipped: main has no
// equivalent, and splitting one side only would compare different geometry. --separate turns
// it back on for the one input where it bites (larynx_bolus, two components).

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using BasicResults;
using GeometryEngine;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;

var inputs = new List<string>();
string? outDir = null;
string? centredDir = null;
string? rawDir = null;
var iterations = 1;
var intensity = 1.0;
var inflation = 0.1;
var remeshRatio = 2.0;
var resolution = 1.0;
var separate = false;
var pipeline = "double-offset";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--centred-out": centredDir = args[++i]; break;
        case "--raw-out": rawDir = args[++i]; break;
        case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--intensity": intensity = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--inflation": inflation = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--remesh-ratio": remeshRatio = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--resolution": resolution = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--separate": separate = true; break;
        case "--pipeline": pipeline = args[++i]; break;
        default: inputs.Add(args[i]); break;
    }
}

if (outDir is null || inputs.Count == 0)
{
    Console.Error.WriteLine("usage: engine-harness --out <dir> [--centred-out <dir>] [--iterations n] " +
                            "[--intensity mm] [--inflation mm] [--remesh-ratio r] [--resolution mm] [--separate]\n" +
                            "                       [--pipeline double-offset|offset-smooth] <input.stl>...");
    return 2;
}

Directory.CreateDirectory(outDir);
if (centredDir is not null) Directory.CreateDirectory(centredDir);
if (rawDir is not null) Directory.CreateDirectory(rawDir);

var engine = BspGeometryEngine.Create();
var records = new List<Record>();
var failed = 0;

foreach (var input in inputs)
{
    var name = Path.GetFileNameWithoutExtension(input);
    var total = Stopwatch.StartNew();

    var imported = engine.IO.Import(input);
    if (imported.IsFailure)
    {
        records.Add(Record.Failure(name, "import", imported.Error.ToString()));
        failed++;
        continue;
    }

    // ImportMesh centres on the bounding box. The cast to float mirrors TranslateCommand,
    // which carries its offset as a System.Numerics.Vector3.
    var pieces = new List<IMesh>();
    if (separate)
    {
        var parts = engine.Evaluators.SeparateComponents(imported.Value);
        if (parts.IsSuccess && parts.Value.Length > 1) pieces.AddRange(parts.Value);
        else pieces.Add(imported.Value);
    }
    else
    {
        pieces.Add(imported.Value);
    }

    var smoothedPieces = new List<IMesh>();
    var centredPieces = new List<IMesh>();
    string? error = null;
    string? offsetProducer = null;
    var pieceNotes = new List<string>();
    double importMs = total.Elapsed.TotalMilliseconds, doubleOffsetMs = 0, inflateMs = 0, decimateMs = 0;

    foreach (var piece in pieces)
    {
        var stats = engine.Evaluators.GetStatistics(piece);
        var centred = piece;
        if (stats.IsSuccess)
        {
            var centre = (stats.Value.BoundsMin + stats.Value.BoundsMax) / 2.0;
            var offset = new Vec3((float)-centre.X, (float)-centre.Y, (float)-centre.Z);
            var moved = engine.Transforms.Translate(piece, offset);
            if (moved.IsSuccess) centred = moved.Value;
        }

        centredPieces.Add(centred);

        var baseTriangles = centred.TriangleCount;
        pieceNotes.Add($"component {pieceNotes.Count + 1}: {baseTriangles} tris");

        var step = Stopwatch.StartNew();
        // offset-smooth swaps the closing for Modifiers.OffsetSmooth, which iterates on the
        // sampled field instead of re-meshing every round. Nothing else about the pipeline moves.
        var offsetResult = pipeline == "offset-smooth"
            ? engine.Modifiers.OffsetSmooth(centred, intensity, iterations, resolution)
            : engine.Modifiers.DoubleOffset(centred, intensity, iterations, resolution);
        doubleOffsetMs += step.Elapsed.TotalMilliseconds;
        if (offsetResult.IsFailure) { error = $"Closing ({pipeline}): {offsetResult.Error}"; break; }

        var current = offsetResult.Value;
        offsetProducer ??= current.Metadata.CreatedBy;
        if (current.TriangleCount == 0) { error = "Smoothing.OverEroded"; break; }

        if (Math.Abs(inflation) > 0.001)
        {
            step.Restart();
            var inflated = engine.Modifiers.Offset(current, inflation, resolution);
            inflateMs += step.Elapsed.TotalMilliseconds;
            if (inflated.IsFailure) { error = $"Offset: {inflated.Error}"; break; }
            current = inflated.Value;
        }

        // The offset surface exactly as the level-set mesher produced it, before Decimate welds
        // or collapses anything: what separates a defect in the offset from one in the decimation.
        if (rawDir is not null && pieces.Count == 1)
        {
            engine.IO.Export(current, Path.Combine(rawDir, name + ".stl"), overwrite: true);
        }

        var target = (int)(baseTriangles * Math.Max(remeshRatio, 1.0));
        step.Restart();
        var resized = engine.Modifiers.Decimate(current, target);
        decimateMs += step.Elapsed.TotalMilliseconds;
        if (resized.IsFailure) { error = $"Decimate: {resized.Error}"; break; }

        smoothedPieces.Add(resized.Value);
    }

    if (error is not null)
    {
        records.Add(Record.Failure(name, "smooth", error + (pieceNotes.Count > 0 ? " [" + string.Join("; ", pieceNotes) + "]" : "")));
        failed++;
        continue;
    }

    var smoothed = Combine(smoothedPieces);
    total.Stop();

    var outPath = Path.Combine(outDir, name + ".stl");
    var written = engine.IO.Export(smoothed, outPath, overwrite: true);
    if (written.IsFailure)
    {
        records.Add(Record.Failure(name, "export", written.Error.ToString()));
        failed++;
        continue;
    }

    if (centredDir is not null)
    {
        engine.IO.Export(Combine(centredPieces), Path.Combine(centredDir, name + ".stl"), overwrite: true);
    }

    records.Add(new Record
    {
        Case = name,
        Ok = true,
        Components = pieces.Count,
        ComponentNotes = pieces.Count > 1 ? pieceNotes : null,
        InputTriangles = centredPieces.Sum(p => p.TriangleCount),
        OutputTriangles = smoothed.TriangleCount,
        OutputVertices = smoothed.VertexCount,
        CreatedBy = smoothed.Metadata.CreatedBy,
        OffsetProducer = offsetProducer,
        ImportMs = Round(importMs),
        DoubleOffsetMs = Round(doubleOffsetMs),
        InflateMs = Round(inflateMs),
        DecimateMs = Round(decimateMs),
        TotalMs = Round(total.Elapsed.TotalMilliseconds),
    });

    Console.Error.WriteLine($"{pipeline} {name}: {smoothed.TriangleCount} tris in {total.Elapsed.TotalSeconds:F1}s");
}

var report = new
{
    engine = "GeometryEngine",
    manifold_available = ManifoldAvailability(),
    native_field_available = NativeFieldAvailability(),
    settings = new { pipeline, iterations, intensity, inflation, remesh_ratio = remeshRatio, resolution, separate },
    cases = records,
};

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
}));

return failed == 0 ? 0 : 1;

static double Round(double ms) => Math.Round(ms, 1);

// Reflection rather than a public API: both availability flags are internal to the engine, and
// the harness only reports them so the run records which code path produced the meshes.
static bool? ManifoldAvailability() => Flag("GeometryEngine.Internal.Native.ManifoldNative");
static bool? NativeFieldAvailability() => Flag("GeometryEngine.Internal.Native.DistanceFieldNative");

static bool? Flag(string typeName)
{
    var type = typeof(BspGeometryEngine).Assembly.GetType(typeName);
    var property = type?.GetProperty("IsAvailable",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    return property?.GetValue(null) as bool?;
}

static IMesh Combine(List<IMesh> pieces)
{
    if (pieces.Count == 1) return pieces[0];

    var vertices = ImmutableArray.CreateBuilder<Vec3>();
    var triangles = ImmutableArray.CreateBuilder<int>();
    foreach (var piece in pieces)
    {
        var offset = vertices.Count;
        vertices.AddRange(piece.Vertices);
        foreach (var index in piece.Triangles) triangles.Add(index + offset);
    }

    return ImmutableMesh.Create(vertices.ToImmutable(), triangles.ToImmutable(), pieces[0].Metadata).Value;
}

internal sealed class Record
{
    public string Case { get; set; } = "";
    public bool Ok { get; set; }
    public string? Stage { get; set; }
    public string? Error { get; set; }
    public int? Components { get; set; }
    public List<string>? ComponentNotes { get; set; }
    public int? InputTriangles { get; set; }
    public int? OutputTriangles { get; set; }
    public int? OutputVertices { get; set; }
    public string? CreatedBy { get; set; }
    public string? OffsetProducer { get; set; }
    public double? ImportMs { get; set; }
    public double? DoubleOffsetMs { get; set; }
    public double? InflateMs { get; set; }
    public double? DecimateMs { get; set; }
    public double? TotalMs { get; set; }

    public static Record Failure(string name, string stage, string message) =>
        new() { Case = name, Ok = false, Stage = stage, Error = message };
}
