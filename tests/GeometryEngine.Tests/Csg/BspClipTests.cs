namespace GeometryEngine.Tests.Csg;

[Suite("Csg / clipping cost")]
public sealed class BspClipTests
{
    /// <summary>
    /// Every face plane of a convex solid has all the other faces behind it, so its BSP is a chain
    /// as deep as it has planes and nothing a divider heuristic picks can shorten it. Clipping one
    /// such tree against another used to allocate two lists at every level a polygon passed,
    /// which made a subtract between two thousand-triangle spheres allocate about 150 MB, and
    /// grow with the square of the triangle count - 3.6 GB between two 5,000-triangle spheres.
    /// The ceiling sits between that and the 43 MB the walk takes now.
    /// </summary>
    [Fact]
    public void Clipping_convex_solids_does_not_allocate_per_level_of_their_trees()
    {
        var engine = BspGeometryEngine.CreateManagedBsp();
        var left = engine.Generators.GenerateSphere(Vec3.Zero, 10, 32).Value;
        var right = engine.Generators.GenerateSphere(new Vec3(4, 0, 0), 6, 32).Value;

        // Warm, so first-use allocations are not counted.
        _ = engine.Booleans.Subtract(left, right).Value;

        const long Ceiling = 80L * 1024 * 1024;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = engine.Booleans.Subtract(left, right).Value;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Check.Greater(result.TriangleCount, 0);
        Check.True(
            allocated <= Ceiling,
            $"Subtracting two 960-triangle spheres allocated {allocated / (1024 * 1024)} MB (ceiling {Ceiling / (1024 * 1024)} MB)");
    }
}
