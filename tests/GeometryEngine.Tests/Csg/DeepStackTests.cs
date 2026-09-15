using GeometryEngine.Internal.Csg;

namespace GeometryEngine.Tests.Csg;

[Suite("Csg / deep recursion")]
public sealed class DeepStackTests
{
    // Far deeper than the ~1 MB default thread stack can hold (a trivial frame
    // overflows it somewhere in the low tens of thousands), but comfortably within
    // the large stack DeepStack reserves. Without the fix this recursion would
    // bring the whole test process down with an uncatchable StackOverflowException.
    private const int DepthBeyondDefaultStack = 200_000;

    private static int Descend(int remaining) => remaining == 0 ? 0 : Descend(remaining - 1) + 1;

    [Fact]
    public void Runs_recursion_far_deeper_than_the_default_stack_allows()
    {
        var depth = DeepStack.Run(() => Descend(DepthBeyondDefaultStack));

        Check.Equal(DepthBeyondDefaultStack, depth);
    }

    [Fact]
    public void A_moderately_tessellated_subtract_no_longer_overflows_the_stack()
    {
        // Two spheres at this tessellation build a BSP thousands of levels deep;
        // on the default stack the kernel's Inverted/ClippedTo walks overflowed.
        var left = Fixtures.Sphere(new Vec3(-0.45, 0, 0), 1, 80);
        var right = Fixtures.Sphere(new Vec3(0.45, 0, 0), 1, 80);

        var result = Fixtures.Engine.Booleans.Subtract(left, right);

        Check.True(result.IsSuccess);
        Check.True(result.Value.TriangleCount > 0);
    }

    [Fact]
    public void An_exception_thrown_on_the_worker_thread_propagates_to_the_caller()
    {
        var threw = false;
        try
        {
            DeepStack.Run<int>(() => throw new InvalidOperationException("boom"));
        }
        catch (InvalidOperationException exception)
        {
            threw = exception.Message == "boom";
        }

        Check.True(threw);
    }
}
