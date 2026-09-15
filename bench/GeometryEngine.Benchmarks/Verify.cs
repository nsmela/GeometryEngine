using System.Diagnostics;
using System.Reflection;
using GeometryEngine.Core.Geometry;
using GeometryEngine;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Checks the boolean operations against every test mesh, not by eyeballing the output
/// but by the set-algebra identities that must hold for any correct result, whatever
/// the tessellation:
///   V(A ∪ B) = V(A) + V(B) − V(A ∩ B)
///   V(A ∖ B) = V(A) − V(A ∩ B)
/// The volumes come from the independently produced result meshes, so agreement is real
/// evidence the operations compose correctly. Watertightness is reported alongside.
///
/// Some real meshes can drive the BSP kernel into pathological recursion that overflows
/// the stack - an uncatchable crash. So each mesh is verified in a child process: a
/// crash there becomes a reported CRASH row rather than taking the whole run down.
/// </summary>
internal static class Verify
{
    private const double Tolerance = 0.005; // 0.5% of the operand volume

    private static readonly TimeSpan PerMeshTimeout = TimeSpan.FromSeconds(90);

    public static int RunAll()
    {
        Console.WriteLine($"{"mesh",-26}{"tris",8}{"∪ resid",9}{"∖ resid",9}{"input",6}{"result watertight",18}{"result",7}  {"kernel",-8} defect profile");
        Console.WriteLine(new string('-', 110));

        var failures = 0;
        foreach (var path in TestMeshes.All())
        {
            var (ok, line) = VerifyInChildProcess(path);
            Console.WriteLine(line);
            Console.Out.Flush();
            if (!ok)
            {
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All meshes satisfy the boolean volume identities."
            : $"{failures} mesh(es) failed, crashed or timed out.");

        return failures == 0 ? 0 : 1;
    }

    private static (bool Ok, string Line) VerifyInChildProcess(string path)
    {
        var name = Path.GetFileName(path);
        var placeholder = $"{name,-26}{"",8}{"",9}{"",9}{"",6}{"",18}";

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("verify-one");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();

        if (!process.WaitForExit((int)PerMeshTimeout.TotalMilliseconds))
        {
            // A boolean that fragments without bound can run for minutes; treat that as
            // a failure of the operation to be usable, not a reason to stall the run.
            process.Kill(entireProcessTree: true);
            return (false, $"{placeholder}{"TIMEOUT",10}");
        }

        return process.ExitCode switch
        {
            // A stack overflow or other native crash exits with neither 0 nor 1.
            0 => (true, output.Result.TrimEnd()),
            1 => (false, output.Result.TrimEnd()),
            _ => (false, $"{placeholder}{"CRASH",10}"),
        };
    }

    /// <summary>Verifies one mesh in-process; prints a single row and returns 0/1.</summary>
    public static int RunOne(string path)
    {
        var engine = BspGeometryEngine.Create();
        var name = Path.GetFileName(path);

        var import = engine.IO.Import(path);
        if (import.IsFailure)
        {
            Console.WriteLine($"{name,-26}  IMPORT FAILED: {import.Error.Code}");
            return 1;
        }

        var mesh = import.Value;
        var tool = TestMeshes.OverlappingSphere(engine, mesh);

        var union = engine.Booleans.Union(mesh, tool);
        var subtract = engine.Booleans.Subtract(mesh, tool);
        var intersect = engine.Booleans.Intersect(mesh, tool);
        if (union.IsFailure || subtract.IsFailure || intersect.IsFailure)
        {
            var err = union.IsFailure ? $"union: {union.Error}"
                    : subtract.IsFailure ? $"subtract: {subtract.Error}"
                    : $"intersect: {intersect.Error}";
            Console.WriteLine($"{name,-26}  OPERATION RETURNED FAILURE ({err})");
            return 1;
        }

        var va = Volume(engine, mesh);
        var vb = Volume(engine, tool);
        var vIntersect = Volume(engine, intersect.Value);

        var scale = Math.Max(Math.Abs(va), 1.0);
        var unionResidual = Math.Abs(Volume(engine, union.Value) - (va + vb - vIntersect)) / scale;
        var subtractResidual = Math.Abs(Volume(engine, subtract.Value) - (va - vIntersect)) / scale;

        var inputWatertight = Topology(engine, mesh);
        var watertight =
            $"u:{Mark(Watertight(engine, union.Value))} -:{Mark(Watertight(engine, subtract.Value))} n:{Mark(Watertight(engine, intersect.Value))}";
        var passed = unionResidual < Tolerance && subtractResidual < Tolerance;

        Console.WriteLine(
            $"{name,-26}{mesh.TriangleCount,8}{unionResidual,9:0.0000}{subtractResidual,9:0.0000}{inputWatertight,6}{watertight,18}{(passed ? "PASS" : "FAIL"),7}  {Producer(subtract.Value),-8} {Defects(engine, subtract.Value)}");

        return passed ? 0 : 1;
    }

    /// <summary>
    /// Which kernel produced a result, abbreviated. Worth a column: only the native kernel
    /// guarantees watertight output, so a row reporting defects reads very differently
    /// depending on whether the native kernel or the managed fallback produced it.
    /// </summary>
    private static string Producer(IMesh mesh) => mesh.Metadata.CreatedBy switch
    {
        "GeometryEngine.Booleans.Manifold" => "native",
        "GeometryEngine.Booleans.Manifold (operands merged)" => "merged",
        "GeometryEngine.Booleans.Bsp (Manifold declined)" => "FALLBACK",
        _ => "bsp",
    };

    private static double Volume(IGeometryEngine engine, IMesh mesh) =>
        engine.Evaluators.GetStatistics(mesh).Value.Volume;

    private static bool Watertight(IGeometryEngine engine, IMesh mesh) =>
        engine.Evaluators.ValidateTopology(mesh).Value.IsWatertight;

    /// <summary>A compact defect profile of one result (here the subtraction).</summary>
    private static string Defects(IGeometryEngine engine, IMesh mesh)
    {
        var t = engine.Evaluators.ValidateTopology(mesh).Value;
        return $"sub[bnd {t.BoundaryEdgeCount} nm {t.NonManifoldEdgeCount} wind {t.InconsistentWindingEdgeCount} dup {t.DuplicateFaceCount} shells {t.ShellCount}]";
    }

    private static string Mark(bool value) => value ? "y" : "N";

    /// <summary>
    /// A mesh's topology in one token, distinguishing the two defects a single "not
    /// watertight" flag used to conflate. They are not comparable in severity: a hole leaves
    /// the solid with no well-defined inside and cannot be printed, while a doubled face or
    /// two sheets touching along an edge still encloses a volume and is usually cleaned away
    /// by the kernel. Reporting both as "N" made a closed mesh with one doubled face look
    /// exactly as broken as a torn one.
    /// </summary>
    private static string Topology(IGeometryEngine engine, IMesh mesh)
    {
        var t = engine.Evaluators.ValidateTopology(mesh).Value;

        return (t.IsClosed, t.IsEdgeManifold) switch
        {
            (true, true) => "y",
            (false, _) => "HOLE",
            (true, false) => "dup",
        };
    }
}
