namespace GeometryEngine.Tests.Modifiers;

[Suite("Modifiers / decimate, pinches")]
public sealed class DecimatePinchTests
{
    /// <summary>
    /// Two boxes touching along one edge, each closed on its own: a 2-manifold by index, as
    /// Manifold's mesher hands back a surface that touches itself, but a weld by position fuses the
    /// shared edge into one carrying four faces.
    /// </summary>
    private static IMesh BoxesTouchingAlongAnEdge() =>
        Assets.Concatenate(
            Fixtures.Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1)),
            Fixtures.Box(new Vec3(1, 1, 0), new Vec3(2, 2, 1)));

    [Fact]
    public void Welding_two_boxes_that_touch_along_an_edge_makes_it_non_manifold()
    {
        // The defect decimation used to introduce, shown on the boxes the other tests use.
        var welded = Fixtures.Engine.Modifiers.Repair(BoxesTouchingAlongAnEdge()).Value;

        Check.Greater(Fixtures.TopologyOf(welded).NonManifoldEdgeCount, 0);
    }

    [Fact]
    public void Decimating_a_surface_that_touches_itself_keeps_the_sheets_apart()
    {
        var boxes = BoxesTouchingAlongAnEdge();

        var kept = Fixtures.Engine.Modifiers.Decimate(boxes, 100).Value;

        Check.Equal(16, kept.VertexCount);
        Check.Equal(0, Fixtures.TopologyOf(kept).NonManifoldEdgeCount);
    }

    [Fact]
    public void Decimating_a_triangle_soup_still_welds_it_whole()
    {
        var soup = Assets.Unwelded(Fixtures.UnitCube());

        var welded = Fixtures.Engine.Modifiers.Decimate(soup, 100).Value;

        Check.Equal(8, welded.VertexCount);
        Check.True(Fixtures.TopologyOf(welded).IsClean);
    }

    [Fact]
    public void Smoothing_a_real_bolus_then_decimating_it_leaves_no_edge_with_three_faces()
    {
        // Fabolus's smoothing, step for step. Its offsets pinch where the level set closes a narrow
        // gap, and welding the copies of each pinch point gave the decimated body edges with three
        // or four faces, which the parting-line trace then refused.
        foreach (var name in new[] { "chin_bolus.stl", "larynx_bolus.stl", "larynx small.stl" })
        {
            var bolus = Assets.LoadBench(name);

            var closed = Fixtures.Engine.Modifiers.DoubleOffset(bolus, 1, iterations: 1, cellSize: 1).Value;
            var inflated = Fixtures.Engine.Modifiers.Offset(closed, 0.1, cellSize: 1).Value;
            var reduced = Fixtures.Engine.Modifiers.Decimate(inflated, bolus.TriangleCount * 2).Value;

            Check.Equal(0, Fixtures.TopologyOf(reduced).NonManifoldEdgeCount);
            Check.True(Fixtures.TopologyOf(reduced).IsWatertight);
        }
    }
}
