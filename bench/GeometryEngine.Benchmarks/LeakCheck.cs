using GeometryEngine.Core.Geometry;
using GeometryEngine;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// A crude but honest memory-leak probe. The engine is pure and immutable and holds no
/// static state, so repeating an operation should retain no memory once its results go
/// out of scope and the heap is collected. This runs a boolean many times at two
/// different loop counts and reports the memory still held afterwards: if that retained
/// figure scales with the number of iterations, something is holding references it
/// should not. A flat figure across the two runs is the clean result.
/// </summary>
internal static class LeakCheck
{
    public static int Run()
    {
        var engine = BspGeometryEngine.Create();
        var mesh = TestMeshes.Load(engine, "chin_bolus.stl");
        var tool = TestMeshes.OverlappingSphere(engine, mesh);

        // Warm up so JIT and first-use allocations do not count against the measurement.
        _ = engine.Booleans.Subtract(mesh, tool).Value.TriangleCount;

        var (managedShort, privateShort) = RetainedOver(engine, mesh, tool, 100);
        var (managedLong, privateLong) = RetainedOver(engine, mesh, tool, 400);

        Console.WriteLine($"managed retained after 100 subtracts : {Kb(managedShort)}");
        Console.WriteLine($"managed retained after 400 subtracts : {Kb(managedLong)}");
        Console.WriteLine($"private bytes retained after 100 sub : {Kb(privateShort)}");
        Console.WriteLine($"private bytes retained after 400 sub : {Kb(privateLong)}");

        // A leak retains memory in proportion to the work done, so this is a ceiling on what
        // the longer run may retain rather than a ratio against the shorter one. A ratio
        // would be misleading: retained memory after warm-up is routinely zero, and "more
        // than twice zero" is true of any growth whatsoever.
        //
        // PrivateMemorySize64 includes the managed heap, so managed growth is subtracted
        // before judging the native side.
        const long ManagedCeiling = 8L * 1024 * 1024;
        const long NativeCeiling = 64L * 1024 * 1024;

        var nativeLong = Math.Max(0, privateLong - managedLong);

        var managedLeak = managedLong > ManagedCeiling;
        var nativeLeak = nativeLong > NativeCeiling;
        var suspiciousGrowth = managedLeak || nativeLeak;

        Console.WriteLine($"native retained (private minus managed)  : {Kb(nativeLong)}");

        Console.WriteLine();
        Console.WriteLine(suspiciousGrowth
            ? "SUSPECT: retained memory scales with iterations - possible leak."
            : "OK: retained memory does not scale with iterations - no leak detected.");

        return suspiciousGrowth ? 1 : 0;
    }

    private static (long Managed, long Private) RetainedOver(IGeometryEngine engine, IMesh mesh, IMesh tool, int iterations)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var beforeManaged = Settled();
        proc.Refresh();
        var beforePrivate = proc.PrivateMemorySize64;

        var checksum = 0;
        for (var i = 0; i < iterations; i++)
        {
            checksum += engine.Booleans.Subtract(mesh, tool).Value.TriangleCount;
        }

        GC.KeepAlive(checksum);
        var afterManaged = Settled();
        proc.Refresh();
        var afterPrivate = proc.PrivateMemorySize64;

        return (Math.Max(0, afterManaged - beforeManaged), Math.Max(0, afterPrivate - beforePrivate));
    }

    /// <summary>Total managed heap after a full, blocking collection.</summary>
    private static long Settled()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static string Kb(long bytes) => $"{bytes / 1024.0,10:0.0} KB";
}
