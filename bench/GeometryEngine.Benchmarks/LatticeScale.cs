using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Measures how boolean accuracy on curved surfaces varies with the model's size relative
/// to the kernel's lattice step. Two equal spheres are united and intersected at several
/// radii and compared against the closed-form answers, so the only thing changing is how
/// many lattice steps the geometry spans.
/// </summary>
internal static class LatticeScale
{
    public static int Run()
    {
        var engine = BspGeometryEngine.Create();

        Console.WriteLine(
            $"{"segments",10} {"tris in",13} {"tris out",12} " +
            $"{"union err",10} {"lens err",10} {"u wt",5} {"n wt",5}");
        Console.WriteLine(new string('-', 74));

        // Segments first, at a fixed printable radius: if the output triangle count grows
        // far faster than the input, the kernel is fragmenting rather than merely deep.
        foreach (var (radius, Segments) in new[]
                 {
                     (100.0, 6), (100.0, 8), (100.0, 12), (100.0, 16), (100.0, 24), (100.0, 48),
                 })
        {
            var left = engine.Generators.GenerateSphere(Vec3.Zero, radius, Segments).Value;
            var right = engine.Generators.GenerateSphere(new Vec3(radius, 0, 0), radius, Segments).Value;

            var union = engine.Booleans.Union(left, right);
            var lens = engine.Booleans.Intersect(left, right);

            if (union.IsFailure || lens.IsFailure)
            {
                Console.WriteLine($"{radius,10:F1} FAILED: {union.Error?.Code ?? lens.Error?.Code}");
                continue;
            }

            // The tessellated sphere under-fills the true sphere, so compare against the
            // measured operand volumes rather than the analytic sphere: that isolates the
            // boolean's error from the tessellation's.
            var leftVolume = engine.Evaluators.GetStatistics(left).Value.Volume;
            var rightVolume = engine.Evaluators.GetStatistics(right).Value.Volume;
            var unionVolume = engine.Evaluators.GetStatistics(union.Value).Value.Volume;
            var lensVolume = engine.Evaluators.GetStatistics(lens.Value).Value.Volume;

            // V(A u B) + V(A n B) = V(A) + V(B), whatever the tessellation.
            var expectedSum = leftVolume + rightVolume;
            var actualSum = unionVolume + lensVolume;
            var identityError = Math.Abs(actualSum - expectedSum) / expectedSum;

            // And the analytic lens, which the tessellation only slightly under-fills.
            var analyticLens = Math.PI * Math.Pow(radius, 2) * (radius + (4 * radius)) / 12.0;
            var lensError = Math.Abs(lensVolume - analyticLens) / analyticLens;

            var unionWatertight = engine.Evaluators.ValidateTopology(union.Value).Value.IsWatertight;
            var lensWatertight = engine.Evaluators.ValidateTopology(lens.Value).Value.IsWatertight;

            Console.WriteLine(
                $"{Segments,10} {left.TriangleCount * 2,13} {union.Value.TriangleCount,12} " +
                $"{identityError * 100,9:F3}% {lensError * 100,9:F3}% " +
                $"{(unionWatertight ? "y" : "N"),5} {(lensWatertight ? "y" : "N"),5}");
        }

        Console.WriteLine();
        Console.WriteLine("union err = |V(AuB) + V(AnB) - V(A) - V(B)| / (V(A)+V(B)), a tessellation-free identity.");
        Console.WriteLine("lens err  = intersection against the closed-form lens volume.");
        return 0;
    }
}
