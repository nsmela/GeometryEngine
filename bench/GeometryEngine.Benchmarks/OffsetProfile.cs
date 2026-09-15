using System.Diagnostics;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Where an offset's time goes: the distance field's per-query cost (closest point alone, and
/// signed with the winding number) against the whole native offset, on real meshes. A plain
/// console run - the offset is seconds, not nanoseconds, so a statistical harness adds nothing.
/// </summary>
internal static class OffsetProfile
{
    public static int Run()
    {
        var engine = BspGeometryEngine.Create();
        Console.WriteLine($"native field available: {DistanceFieldNative.IsAvailable}");

        foreach (var name in new[] { "sphere.stl", "ear_bolus.stl", "test bolus 107mL.stl" })
        {
            var mesh = engine.IO.Import(TestMeshes.PathOf(name)).Value;
            var stats = engine.Evaluators.GetStatistics(mesh).Value;
            Console.WriteLine($"\n{name}: {mesh.TriangleCount} tris, size {stats.BoundsSize}");

            var clock = Stopwatch.StartNew();
            using var field = DistanceFieldHandle.Create(mesh)!;
            Console.WriteLine($"  field build        {clock.ElapsedMilliseconds,6} ms");

            var random = new Random(7);
            const int count = 200_000;
            var flat = new double[count * 3];
            for (var i = 0; i < count; i++)
            {
                flat[i * 3] = stats.BoundsMin.X + (random.NextDouble() * stats.BoundsSize.X);
                flat[(i * 3) + 1] = stats.BoundsMin.Y + (random.NextDouble() * stats.BoundsSize.Y);
                flat[(i * 3) + 2] = stats.BoundsMin.Z + (random.NextDouble() * stats.BoundsSize.Z);
            }

            var distances = new double[count];
            clock.Restart();
            DistanceFieldNative.ge_field_closest_point(field, flat, count, null, null, distances);
            Console.WriteLine($"  200k closest       {clock.ElapsedMilliseconds,6} ms");

            clock.Restart();
            DistanceFieldNative.ge_field_signed_distance(field, flat, count, distances);
            Console.WriteLine($"  200k signed        {clock.ElapsedMilliseconds,6} ms");

            foreach (var cell in new[] { 1.0, 0.5 })
            {
                clock.Restart();
                var offset = engine.Modifiers.Offset(mesh, 1, cell);
                Console.WriteLine($"  offset cell {cell,3}    {clock.ElapsedMilliseconds,6} ms  -> {(offset.IsSuccess ? offset.Value.TriangleCount : -1)} tris");
            }
        }

        return 0;
    }
}
