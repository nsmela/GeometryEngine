using System.Diagnostics;

namespace GeometryEngine.Tests.Booleans;

[Suite("Booleans / memory and leak checks")]
public sealed class MemoryLeakTests
{
    [Fact]
    public void Repeated_booleans_do_not_leak_native_or_managed_memory()
    {
        var engine = Fixtures.Engine;
        var a = Fixtures.Sphere(Vec3.Zero, 10, 16);
        var b = Fixtures.Sphere(new Vec3(5, 0, 0), 10, 16);

        // Warm up JIT, native runtime, and initial CRT/GC heap segments
        for (var i = 0; i < 20; i++)
        {
            _ = engine.Booleans.Union(a, b).Value.TriangleCount;
            _ = engine.Booleans.Subtract(a, b).Value.TriangleCount;
            _ = engine.Booleans.Intersect(a, b).Value.TriangleCount;
        }

        // Measure memory retained across a moderate run (100 cycles = 300 ops)
        // vs a run 3x longer (300 cycles = 900 ops).
        var (managed1, private1) = MeasureRetained(engine, a, b, 100);
        var (managed2, private2) = MeasureRetained(engine, a, b, 300);

        // A leak retains memory in proportion to the work done, so the test is a ceiling on
        // what the longer run may retain - not a ratio against the shorter one. The ratio is
        // tempting and wrong: retained memory after a warm-up is routinely zero, and
        // "grew more than twice zero" is true of any growth at all, which makes the check
        // fire on an ordinary heap expansion and stay silent on a real but modest leak.
        //
        // The ceilings are set against what a leak here would actually cost. Each cycle
        // marshals both operands and the result across the boundary, tens of thousands of
        // triangles apiece; failing to free that would retain hundreds of megabytes over 900
        // operations, far above these figures.
        const long ManagedCeiling = 8L * 1024 * 1024;
        const long NativeCeiling = 64L * 1024 * 1024;

        // PrivateMemorySize64 counts the managed heap too, so the managed growth is taken
        // out before judging the native side; otherwise ordinary GC growth reads as a
        // native leak.
        var native1 = Math.Max(0, private1 - managed1);
        var native2 = Math.Max(0, private2 - managed2);

        Check.True(
            managed2 <= ManagedCeiling,
            $"Managed memory retained after 900 operations: {managed2 / 1024}KB " +
            $"(ceiling {ManagedCeiling / 1024}KB; 300 operations retained {managed1 / 1024}KB)");

        Check.True(
            native2 <= NativeCeiling,
            $"Native memory retained after 900 operations: {native2 / 1024}KB " +
            $"(ceiling {NativeCeiling / 1024}KB; 300 operations retained {native1 / 1024}KB)");
    }

    private static (long Managed, long Private) MeasureRetained(
        IGeometryEngine engine,
        IMesh a,
        IMesh b,
        int cycles)
    {
        var proc = Process.GetCurrentProcess();
        var beforeManaged = Settle();
        proc.Refresh();
        var beforePrivate = proc.PrivateMemorySize64;

        var checksum = 0;
        for (var i = 0; i < cycles; i++)
        {
            checksum += engine.Booleans.Union(a, b).Value.TriangleCount;
            checksum += engine.Booleans.Subtract(a, b).Value.TriangleCount;
            checksum += engine.Booleans.Intersect(a, b).Value.TriangleCount;
        }

        GC.KeepAlive(checksum);
        var afterManaged = Settle();
        proc.Refresh();
        var afterPrivate = proc.PrivateMemorySize64;

        return (Math.Max(0, afterManaged - beforeManaged), Math.Max(0, afterPrivate - beforePrivate));
    }

    private static long Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
