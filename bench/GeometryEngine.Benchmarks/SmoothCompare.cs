using System.Diagnostics;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Fabolus's smoothing, timed and measured two ways on the same meshes and settings.
///
/// <b>offsets</b> is what Fabolus does today: <c>DoubleOffset</c> (out by the intensity, back by
/// it, once per iteration, re-meshing after every step), then the small inflation offset. That is
/// 2N + 1 level-set meshings and as many field builds. <b>grid</b> is the same closing held on a
/// sampled distance field, <c>OffsetSmooth</c>: one field sample, the iterations on the grid, one
/// meshing. Both are followed by Fabolus's decimation to twice the input count, timed separately,
/// so the closing itself can be compared without it.
///
/// The grid route's own phases are timed through the handler's steps, so a regression in any one
/// of them shows here rather than only in the total. Volume and the deviation from the input say
/// whether the two still agree on what smoothing is; neither is a pass/fail.
/// </summary>
internal static class SmoothCompare
{
    /// <summary>
    /// Fabolus's <c>SmoothSettings</c> defaults: intensity, inflation and cell in millimetres. The
    /// intensity can be overridden from the command line - the app's own preference default is 2.
    /// </summary>
    private static double Intensity = 1.0;
    private const double Inflation = 0.1;
    private const double Cell = 1.0;
    private const double RemeshRatio = 2.0;

    private static readonly string[] DefaultMeshes =
        ["ear_bolus.stl", "larynx small.stl", "test bolus 107mL.stl", "chin_bolus.stl", "scalp_bolus.stl"];

    public static int Run(string[] args)
    {
        if (args.Length > 1 && args[1] == "spheres")
        {
            var cell = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : Cell;
            return Spheres(cell);
        }

        if (args.Length > 1 && args[1] == "iterations")
        {
            Intensity = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : Intensity;
            return IterationStudy(args.Length > 2 && args[2] != "default" ? args[2].Split(',') : DefaultMeshes);
        }

        if (args.Length > 1 && args[1] == "plan")
        {
            Intensity = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : Intensity;
            return Plans(args.Length > 2 && args[2] != "default" ? args[2] : null);
        }

        var iterations = args.Length > 1 ? int.Parse(args[1]) : 1;
        var runs = args.Length > 2 ? int.Parse(args[2]) : 3;
        var meshes = args.Length > 3 && args[3] != "default" ? args[3].Split(',') : DefaultMeshes;
        Intensity = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : Intensity;

        var engine = BspGeometryEngine.Create();
        Console.WriteLine($"native field: {DistanceFieldNative.IsAvailable}   manifold: {ManifoldNative.IsAvailable}");
        Console.WriteLine($"intensity {Intensity} mm, inflation {Inflation} mm, cell {Cell} mm, {iterations} iteration(s), median of {runs}");

        // One untimed pass of each route, so the timed ones do not pay for first-call compilation.
        var warm = TestMeshes.Load(engine, "eye_bolus.stl");
        Offsets(engine, warm, iterations);
        Grid(engine, warm, iterations);

        foreach (var name in meshes)
        {
            var mesh = TestMeshes.Load(engine, name);
            var input = engine.Evaluators.GetStatistics(mesh).Value;
            Console.WriteLine($"\n{name}: {mesh.TriangleCount} tris, volume {input.Volume:F0} mm³");

            var offsetRuns = Enumerable.Range(0, runs).Select(_ => Offsets(engine, mesh, iterations)).ToList();
            var gridRuns = Enumerable.Range(0, runs).Select(_ => Grid(engine, mesh, iterations)).ToList();

            Report(engine, "offsets", mesh, input, offsetRuns);
            Report(engine, "grid", mesh, input, gridRuns);
            Console.WriteLine($"  band sampling's mesh from full sampling's: {BandApartFromFull(engine, mesh, iterations):E1} mm at most");
        }

        return 0;
    }

    private sealed record Outcome(IMesh? Smoothed, IMesh? Decimated, string? Error, (string Phase, double Ms)[] Phases);

    private static Outcome Offsets(IGeometryEngine engine, IMesh mesh, int iterations)
    {
        var phases = new List<(string, double)>();
        var clock = Stopwatch.StartNew();

        var closed = engine.Modifiers.DoubleOffset(mesh, Intensity, iterations, Cell);
        phases.Add(("double offset", clock.Elapsed.TotalMilliseconds));
        if (closed.IsFailure)
        {
            return new Outcome(null, null, closed.Error.ToString(), [.. phases]);
        }

        clock.Restart();
        var inflated = engine.Modifiers.Offset(closed.Value, Inflation, Cell);
        phases.Add(("inflation", clock.Elapsed.TotalMilliseconds));
        if (inflated.IsFailure)
        {
            return new Outcome(null, null, inflated.Error.ToString(), [.. phases]);
        }

        return Decimate(engine, mesh, inflated.Value, phases);
    }

    private static Outcome Grid(IGeometryEngine engine, IMesh mesh, int iterations)
    {
        var phases = new List<(string, double)>();
        var clock = Stopwatch.StartNew();

        var plan = Modifiers.OffsetSmoothHandler.Plan(mesh, Intensity, Cell, Inflation);
        if (plan.IsFailure)
        {
            return new Outcome(null, null, plan.Error.ToString(), []);
        }

        var grid = Modifiers.OffsetSmoothHandler.SampleField(mesh, plan.Value, Intensity);
        phases.Add(("sample", clock.Elapsed.TotalMilliseconds));

        clock.Restart();
        Modifiers.OffsetSmoothHandler.Close(grid, Intensity, iterations, Modifiers.OffsetSmoothHandler.Band(Intensity, Inflation, plan.Value.Cell), Inflation);
        grid.Shift(Inflation);
        phases.Add(("close", clock.Elapsed.TotalMilliseconds));

        clock.Restart();
        var meshed = Modifiers.OffsetSmoothHandler.Mesh(grid, plan.Value, mesh.Metadata);
        phases.Add(("mesh", clock.Elapsed.TotalMilliseconds));
        if (meshed.IsFailure)
        {
            return new Outcome(null, null, meshed.Error.ToString(), [.. phases]);
        }

        phases.Add(($"nodes {grid.CellCount / 1000}k @ {plan.Value.Cell:F2} mm", double.NaN));
        return Decimate(engine, mesh, meshed.Value, phases);
    }

    private static Outcome Decimate(IGeometryEngine engine, IMesh input, IMesh smoothed, List<(string, double)> phases)
    {
        var clock = Stopwatch.StartNew();
        var target = (int)(input.TriangleCount * Math.Max(RemeshRatio, 1.0));
        var decimated = engine.Modifiers.Decimate(smoothed, target);
        phases.Add(("decimate", clock.Elapsed.TotalMilliseconds));

        return decimated.IsFailure
            ? new Outcome(smoothed, null, decimated.Error.ToString(), [.. phases])
            : new Outcome(smoothed, decimated.Value, null, [.. phases]);
    }

    private static void Report(IGeometryEngine engine, string label, IMesh input, MeshStatistics inputStats, List<Outcome> runs)
    {
        var failed = runs.FirstOrDefault(run => run.Error is not null);
        if (failed is not null)
        {
            Console.WriteLine($"  {label,-8} FAILED: {failed.Error}");
            return;
        }

        var phaseNames = runs[0].Phases.Select(phase => phase.Phase).ToArray();
        var medians = phaseNames.Select((phase, i) => (phase, Median(runs.Select(run => run.Phases[i].Ms)))).ToArray();
        var closing = medians.Where(m => m.phase != "decimate" && !double.IsNaN(m.Item2)).Sum(m => m.Item2);
        var total = medians.Where(m => !double.IsNaN(m.Item2)).Sum(m => m.Item2);

        var smoothed = runs[0].Smoothed!;
        var final = runs[0].Decimated!;
        var stats = engine.Evaluators.GetStatistics(final).Value;
        var deviation = engine.Evaluators.MeasureDeviation(final, input).Value;
        var topology = engine.Evaluators.ValidateTopology(final).Value;
        var repeatable = runs.All(run => SameMesh(run.Decimated!, final));

        Console.WriteLine(
            $"  {label,-8} closing {closing,7:F0} ms   total {total,7:F0} ms   " +
            $"volume {100 * ((stats.Volume / inputStats.Volume) - 1),+6:F2} %   " +
            $"deviation mean {deviation.MeanAbsolute:F3} rms {deviation.RootMeanSquare:F3} " +
            $"out {deviation.MaxOutside:F2} in {deviation.MaxInside:F2} mm   " +
            $"{smoothed.TriangleCount} -> {final.TriangleCount} tris   " +
            $"{(topology.BoundaryEdgeCount == 0 && topology.NonManifoldEdgeCount == 0 ? "closed" : "OPEN")}   " +
            $"{(repeatable ? "repeatable" : "NOT REPEATABLE")}");
        Console.WriteLine("           " + string.Join("   ", medians.Select(m => double.IsNaN(m.Item2) ? m.phase : $"{m.phase} {m.Item2:F0}")));
    }

    /// <summary>
    /// Radius error of each route on spheres, which have nothing to fill: anything a closing does
    /// to one is error, or the curvature smoothing the re-meshing adds. Intensity 1 mm, no
    /// inflation, at the given cell. The offsets route's shrink per iteration times the radius
    /// is its curvature-smoothing coefficient, printed as c.
    /// </summary>
    private static int Spheres(double cell)
    {
        var engine = BspGeometryEngine.Create();
        double Radius(IMesh m) => Math.Cbrt(3 * engine.Evaluators.GetStatistics(m).Value.Volume / (4 * Math.PI));
        var intensity = Math.Max(1, cell);
        Console.WriteLine($"cell {cell} mm, intensity {intensity} mm, no inflation: radius change in mm");

        foreach (var r in new[] { 4.0, 8.0, 16.0, 32.0 })
        {
            var sphere = engine.Generators.GenerateSphere(Vec3.Zero, r, 128).Value;
            var r0 = Radius(sphere);
            var line = $"r {r0,6:F2}:";
            double perRound = 0;
            foreach (var n in new[] { 1, 2, 4 })
            {
                var clock = Stopwatch.StartNew();
                var offsets = engine.Modifiers.DoubleOffset(sphere, intensity, n, cell).Value;
                var offsetsMs = clock.ElapsedMilliseconds;
                clock.Restart();
                var grid = engine.Modifiers.OffsetSmooth(sphere, intensity, n, cell).Value;
                var gridMs = clock.ElapsedMilliseconds;
                var shrink = Radius(offsets) - r0;
                if (n == 4) { perRound = shrink / 4; }
                line += $"   x{n} offsets {shrink:+0.000;-0.000} grid {Radius(grid) - r0:+0.000;-0.000} ({offsetsMs}/{gridMs} ms)";
            }

            Console.WriteLine($"{line}   c {-perRound * r0:F3} mm²");
        }

        return 0;
    }

    /// <summary>
    /// What each added iteration changes, both ways: the volume and deviation from the input, how
    /// far the surface moved since one iteration fewer, and how rough it is (mean distance of a
    /// vertex from its neighbours' centroid, over the mean edge length). For the grid it also
    /// counts the nodes the added round changed, which is the closing's own answer to whether
    /// another round has anything left to do.
    /// </summary>
    private static int IterationStudy(string[] meshes)
    {
        var engine = BspGeometryEngine.Create();
        Console.WriteLine($"intensity {Intensity} mm, inflation {Inflation} mm, cell {Cell} mm, before decimation");

        foreach (var name in meshes)
        {
            var mesh = TestMeshes.Load(engine, name);
            var input = engine.Evaluators.GetStatistics(mesh).Value;
            Console.WriteLine($"\n{name}: {mesh.TriangleCount} tris, roughness {Roughness(mesh):F3}");

            var offsetsAt = new Dictionary<int, IMesh>();
            foreach (var route in new[] { "offsets", "grid" })
            {
                IMesh? previous = null;
                for (var n = 1; n <= 4; n++)
                {
                    var result = route == "offsets"
                        ? engine.Modifiers.DoubleOffset(mesh, Intensity, n, Cell)
                            .Bind(closed => engine.Modifiers.Offset(closed, Inflation, Cell))
                        : engine.Modifiers.OffsetSmooth(mesh, Intensity, n, Cell, Inflation);
                    if (result.IsFailure)
                    {
                        Console.WriteLine($"  {route,-8} x{n}  FAILED: {result.Error}");
                        break;
                    }

                    var smoothed = result.Value;
                    var stats = engine.Evaluators.GetStatistics(smoothed).Value;
                    var fromInput = engine.Evaluators.MeasureDeviation(smoothed, mesh).Value;
                    var moved = previous is null ? "" : Moved(engine, smoothed, previous);
                    if (route == "offsets")
                    {
                        offsetsAt[n] = smoothed;
                    }
                    else if (offsetsAt.TryGetValue(n, out var reference))
                    {
                        var apart = engine.Evaluators.MeasureDeviation(smoothed, reference).Value;
                        moved += $"   vs offsets x{n}: mean {apart.MeanAbsolute:F3} out {apart.MaxOutside:F2} in {apart.MaxInside:F2}";
                    }

                    Console.WriteLine(
                        $"  {route,-8} x{n}  volume {100 * ((stats.Volume / input.Volume) - 1),+6:F2} %   " +
                        $"from input mean {fromInput.MeanAbsolute:F3} out {fromInput.MaxOutside:F2} in {fromInput.MaxInside:F2}   " +
                        $"roughness {Roughness(smoothed):F3}   {moved}");
                    previous = smoothed;
                }
            }

            GridRounds(mesh);
        }

        return 0;
    }

    private static string Moved(IGeometryEngine engine, IMesh current, IMesh previous)
    {
        var d = engine.Evaluators.MeasureDeviation(current, previous).Value;
        return $"moved since one fewer: mean {d.MeanAbsolute:F3} max {Math.Max(d.MaxOutside, d.MaxInside):F2}";
    }

    /// <summary>How many grid nodes each added closing round changes, and how many change sign.</summary>
    private static void GridRounds(IMesh mesh)
    {
        var plan = Modifiers.OffsetSmoothHandler.Plan(mesh, Intensity, Cell, Inflation).Value;
        var band = Modifiers.OffsetSmoothHandler.Band(Intensity, Inflation, plan.Cell);
        double[] Closed(int rounds)
        {
            var grid = Modifiers.OffsetSmoothHandler.SampleField(mesh, plan, Intensity);
            Modifiers.OffsetSmoothHandler.Close(grid, Intensity, rounds, band);
            return grid.Values.ToArray();
        }

        var line = new List<string>();
        var before = Closed(1);
        for (var n = 2; n <= 4; n++)
        {
            var after = Closed(n);
            int changed = 0, flipped = 0;
            double largest = 0;
            for (var i = 0; i < before.Length; i++)
            {
                var delta = Math.Abs(after[i] - before[i]);
                if (delta > 1e-12) { changed++; largest = Math.Max(largest, delta); }
                if ((after[i] < 0) != (before[i] < 0)) { flipped++; }
            }

            line.Add($"round {n}: {changed} nodes changed (largest {largest:F4} mm), {flipped} flipped sign");
            before = after;
        }

        Console.WriteLine($"  grid of {before.Length / 1000}k nodes - " + string.Join("; ", line));
    }

    /// <summary>Mean distance of each vertex from its neighbours' centroid, over the mean edge length.</summary>
    private static double Roughness(IMesh mesh)
    {
        var neighbours = new HashSet<int>[mesh.VertexCount];
        for (var v = 0; v < neighbours.Length; v++) { neighbours[v] = []; }
        var t = mesh.Triangles;
        double edgeSum = 0;
        long edges = 0;
        for (var i = 0; i < t.Length; i += 3)
        {
            for (var e = 0; e < 3; e++)
            {
                int a = t[i + e], b = t[i + ((e + 1) % 3)];
                if (neighbours[a].Add(b)) { edgeSum += (mesh.Vertices[a] - mesh.Vertices[b]).Length; edges++; }
                neighbours[b].Add(a);
            }
        }

        double sum = 0;
        var counted = 0;
        for (var v = 0; v < neighbours.Length; v++)
        {
            if (neighbours[v].Count == 0) { continue; }
            var centroid = neighbours[v].Aggregate(Vec3.Zero, (acc, n) => acc + mesh.Vertices[n]) * (1.0 / neighbours[v].Count);
            sum += (mesh.Vertices[v] - centroid).Length;
            counted++;
        }

        return sum / counted / (edgeSum / edges);
    }

    /// <summary>
    /// The grid each mesh in a folder would close on at Fabolus's defaults, or why it would refuse:
    /// the node budget coarsens the cell on a large model, and past the intensity the closing
    /// cannot resolve it.
    /// </summary>
    private static int Plans(string? folder)
    {
        var engine = BspGeometryEngine.Create();
        var paths = folder is null ? TestMeshes.All() : Directory.EnumerateFiles(folder, "*.stl").OrderBy(path => path);
        foreach (var path in paths)
        {
            var imported = engine.IO.Import(path);
            if (imported.IsFailure)
            {
                Console.WriteLine($"{Path.GetFileName(path),-34} import failed: {imported.Error}");
                continue;
            }

            var mesh = imported.Value;
            var size = engine.Evaluators.GetStatistics(mesh).Value.BoundsSize;
            var plan = Modifiers.OffsetSmoothHandler.Plan(mesh, Intensity, Cell, Inflation);
            Console.WriteLine(
                $"{Path.GetFileName(path),-34} {mesh.TriangleCount,7} tris  {size.X,6:F0} x {size.Y,4:F0} x {size.Z,4:F0} mm  " +
                (plan.IsSuccess ? $"cell {plan.Value.Cell:F2} mm" : $"REFUSED {plan.Error.Code}"));
        }

        return 0;
    }

    /// <summary>
    /// How far the mesh closed from the band-sampled field lies from the one closed from a fully
    /// sampled field. Zero, to rounding, unless the mesh's inside and outside disagree somewhere
    /// off the surface - and even then only out where full sampling keeps the stray signs.
    /// </summary>
    private static double BandApartFromFull(IGeometryEngine engine, IMesh mesh, int iterations)
    {
        var plan = Modifiers.OffsetSmoothHandler.Plan(mesh, Intensity, Cell, Inflation).Value;
        var band = Modifiers.OffsetSmoothHandler.Band(Intensity, Inflation, plan.Cell);
        var full = Internal.Smoothing.SignedDistanceGrid.Sample(
            new Internal.Spatial.MeshBvh(mesh).SignedDistance, plan.Min, plan.Max, plan.Cell);
        var near = Modifiers.OffsetSmoothHandler.SampleField(mesh, plan, Intensity);

        Modifiers.OffsetSmoothHandler.Close(full, Intensity, iterations, band);
        Modifiers.OffsetSmoothHandler.Close(near, Intensity, iterations, band);
        var apart = engine.Evaluators.MeasureDeviation(
            Modifiers.OffsetSmoothHandler.Mesh(near, plan, mesh.Metadata).Value,
            Modifiers.OffsetSmoothHandler.Mesh(full, plan, mesh.Metadata).Value).Value;
        return Math.Max(apart.MaxOutside, apart.MaxInside);
    }

    private static bool SameMesh(IMesh a, IMesh b) =>
        a.Vertices.SequenceEqual(b.Vertices) && a.Triangles.SequenceEqual(b.Triangles);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
