using System.Diagnostics;
using System.Runtime.InteropServices;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Times the same operation call after call from a cold process, to show what the first calls
/// cost against the ones that follow.
///
/// .NET compiles a method quickly and badly the first time it runs, and again properly once it
/// has been called enough. An interactive caller makes each of these calls a handful of times a
/// session, so it may never see the second compilation. On one core the first calls measured
/// three to six times the settled figure; on several, where the recompilation has a core of its
/// own, the gap should be narrower. This run says how wide it is on the machine it runs on.
///
/// Run it once as it is and once with <c>DOTNET_TieredCompilation=0</c>, which compiles
/// everything properly the first time: if the first column then falls to the last, compilation is
/// what the first calls are paying for, and publishing the caller with ReadyToRun removes it.
///
/// Every call is on a mesh nothing has measured, and the engine keeps no native solids, so no
/// call rides on work an earlier one left behind.
/// </summary>
internal static class WarmupProfile
{
    private const int Calls = 40;
    private static readonly int[] Shown = [1, 2, 3, 5, 10, 20, 40];

    public static int Run()
    {
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
        Console.WriteLine($"logical processors: {Environment.ProcessorCount}");
        Console.WriteLine(
            $"DOTNET_TieredCompilation: {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "(default)"}" +
            $"   DOTNET_TieredPGO: {Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "(default)"}" +
            $"   DOTNET_ReadyToRun: {Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "(default)"}");
        Console.WriteLine();

        var engine = BspGeometryEngine.CreateWithManifold(SolidRetention.None);
        var bytes = File.ReadAllBytes(TestMeshes.PathOf("test_smoothed_bolus.stl"));

        Console.WriteLine($"{"milliseconds at call",-24}" + string.Concat(Shown.Select(call => $"{call,9}")) + "   first / settled");

        // The first row is also the first thing the process does, as it is for a caller.
        Row("read binary STL", () => () => engine.IO.Read(bytes, MeshFileFormat.Stl, "bolus"));

        var bolus = engine.IO.Read(bytes, MeshFileFormat.Stl, "bolus").Value;
        var stats = engine.Evaluators.GetStatistics(bolus).Value;
        var tool = engine.Generators
            .GenerateSphere((stats.BoundsMin + stats.BoundsMax) * 0.5, stats.BoundsSize.X * 0.3, 32).Value;
        Console.WriteLine($"  ({bolus.TriangleCount:N0} triangles)");

        IMesh Unseen(IMesh mesh) => engine.CreateMesh(mesh.Vertices, mesh.Triangles, mesh.Metadata).Value;

        Row("statistics", () =>
        {
            var mesh = Unseen(bolus);
            return () => engine.Evaluators.GetStatistics(mesh);
        });
        Row("topology audit", () =>
        {
            var mesh = Unseen(bolus);
            return () => engine.Evaluators.ValidateTopology(mesh);
        });
        Row("build spatial index", () =>
        {
            var mesh = Unseen(bolus);
            return () =>
            {
                using var index = engine.Spatial.BuildIndex(mesh).Value;
            };
        });
        Row("1,000 closest points", () =>
        {
            var index = engine.Spatial.BuildIndex(Unseen(bolus)).Value;
            return () =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    _ = index.ClosestPoint(bolus.Vertices[i * 37 % bolus.VertexCount] + new Vec3(0.3, 0.2, 0.1));
                }

                index.Dispose();
            };
        });

        // The first of these also loads the native library, as a caller's first boolean does.
        Row("subtract a sphere", () =>
        {
            var (body, sphere) = (Unseen(bolus), Unseen(tool));
            return () => engine.Booleans.Subtract(body, sphere);
        });
        Row("translate", () =>
        {
            var mesh = Unseen(bolus);
            return () => engine.Transforms.Translate(mesh, new Vec3(1, 2, 3));
        });
        Row("write binary STL", () => () => engine.IO.Write(bolus, MeshFileFormat.Stl));

        return 0;
    }

    /// <param name="prepare">Builds one call's inputs, untimed, and returns the call to time.</param>
    private static void Row(string label, Func<Action> prepare)
    {
        var times = new double[Calls];
        for (var call = 0; call < Calls; call++)
        {
            var work = prepare();
            var watch = Stopwatch.StartNew();
            work();
            times[call] = watch.Elapsed.TotalMilliseconds;
        }

        // Settled: the middle of the last ten, by which time anything called this often has been
        // compiled as well as it is going to be.
        var settled = times[^10..].Order().ElementAt(5);
        Console.WriteLine(
            $"{label,-24}" + string.Concat(Shown.Select(call => $"{times[call - 1],9:N1}")) + $"   {times[0] / settled,8:N1}x");
    }
}
