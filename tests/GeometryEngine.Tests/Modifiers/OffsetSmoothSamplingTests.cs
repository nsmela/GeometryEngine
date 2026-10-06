using GeometryEngine.Internal.Smoothing;
using GeometryEngine.Internal.Spatial;
using GeometryEngine.Modifiers;

namespace GeometryEngine.Tests.Modifiers;

/// <summary>
/// A closing only ever reads an exact distance close to the surface: within the distance it
/// inflates by, plus a margin of cells. Everywhere else it needs to know one thing about a node,
/// which is the side of the surface it is on. Measuring every node exactly was most of the
/// operation's time, and most of those measurements were of nodes whose distance is never read.
///
/// These tests hold the cheaper sampling to the exact one: the same distances where a distance
/// is read, the same side everywhere else, and in the end the same grid to the last bit.
/// </summary>
[Suite("Modifiers / offset smoothing: sampling near the surface")]
public sealed class OffsetSmoothSamplingTests
{
    [Fact]
    public void A_surface_within_reach_is_measured_exactly_and_one_beyond_it_is_not_found()
    {
        var random = new Random(41);

        foreach (var mesh in new[] { Fixtures.Sphere(new Vec3(3, -2, 5), 10, 32), Slotted(), Shell() })
        {
            var bvh = new MeshBvh(mesh);
            var (near, far) = (0, 0);

            foreach (var reach in new[] { 0.5, 2.0, 7.5 })
            {
                for (var i = 0; i < 4000; i++)
                {
                    var point = new Vec3(
                        (random.NextDouble() * 50) - 15, (random.NextDouble() * 50) - 15, (random.NextDouble() * 50) - 15);
                    var exact = bvh.SignedDistance(point);

                    if (bvh.TrySignedDistance(point, reach, out var found))
                    {
                        // The same answer to the last bit, not a near one: the closing interpolates
                        // crossings from these and its result is compared bit for bit below.
                        Check.Equal(BitConverter.DoubleToInt64Bits(exact), BitConverter.DoubleToInt64Bits(found));
                        Check.Less(Math.Abs(exact), reach);
                        near++;
                    }
                    else
                    {
                        Check.GreaterOrEqual(Math.Abs(exact), reach);
                        far++;
                    }
                }
            }

            // Both answers have to have been asked for, or the test says nothing about one of them.
            Check.Greater(near, 500);
            Check.Greater(far, 500);
        }
    }

    [Fact]
    public void Near_the_surface_the_grid_holds_exact_distances_and_beyond_it_the_right_side()
    {
        // A sphere, a concave solid, one with a hollow in it that a row passes into and out of
        // twice, and a scanned bolus.
        foreach (var mesh in new[] { Fixtures.Sphere(Vec3.Zero, 10, 32), Slotted(), Shell(), Assets.LoadBench("ear_bolus.stl") })
        {
            var (min, max, cell, reach) = OffsetSmoothHandler.GridFor(mesh, 2, 0).Value;
            var bvh = new MeshBvh(mesh);

            var exact = SignedDistanceGrid.Sample(bvh.SignedDistance, min, max, cell).Values;
            var sampled = SignedDistanceGrid.SampleNear(bvh, min, max, cell, reach).Values;

            Check.Equal(exact.Length, sampled.Length);
            var (near, inside, outside) = (0, 0, 0);
            for (var node = 0; node < exact.Length; node++)
            {
                if (Math.Abs(exact[node]) < reach)
                {
                    Check.Equal(BitConverter.DoubleToInt64Bits(exact[node]), BitConverter.DoubleToInt64Bits(sampled[node]));
                    near++;
                }
                else
                {
                    // Beyond reach only the side is kept, at the reach itself: far enough that no
                    // crossing is ever interpolated from it.
                    Check.Equal(exact[node] < 0 ? -reach : reach, sampled[node]);
                    _ = exact[node] < 0 ? inside++ : outside++;
                }
            }

            Check.Greater(near, 0);
            Check.Greater(outside, 0);
        }

        // The hollow shell is the one with nodes deep inside the solid: thick enough that some
        // are out of reach of both its walls.
        var shell = Shell();
        var plan = OffsetSmoothHandler.GridFor(shell, 1.5, 0.75).Value;
        var deep = SignedDistanceGrid.SampleNear(new MeshBvh(shell), plan.Min, plan.Max, plan.Cell, plan.Reach).Values;
        Check.True(deep.Contains(-plan.Reach));
    }

    [Fact]
    public void A_box_drawn_inside_the_solid_still_gets_the_right_side()
    {
        // Rows that begin inside the solid, with no surface in reach: nothing before them in the
        // row says which side they are on, so the first node has to be asked.
        var sphere = Fixtures.Sphere(Vec3.Zero, 20, 32);
        var bvh = new MeshBvh(sphere);
        var (min, max) = (new Vec3(-6, -6, -6), new Vec3(6, 6, 30));
        const double Cell = 1;
        const double Reach = 3;

        var exact = SignedDistanceGrid.Sample(bvh.SignedDistance, min, max, Cell).Values;
        var sampled = SignedDistanceGrid.SampleNear(bvh, min, max, Cell, Reach).Values;

        var (inside, outside) = (0, 0);
        for (var node = 0; node < exact.Length; node++)
        {
            Check.Equal(Math.Sign(exact[node]), Math.Sign(sampled[node]));
            _ = exact[node] < 0 ? inside++ : outside++;
        }

        Check.Greater(inside, 100);
        Check.Greater(outside, 100);
    }

    [Fact]
    public void A_reach_no_wider_than_a_cell_is_refused()
    {
        // One node's side is carried to the next only because the surface cannot get from beyond
        // reach on one side to beyond it on the other within a single cell.
        var bvh = new MeshBvh(Fixtures.Sphere(Vec3.Zero, 10, 16));

        Check.Throws<ArgumentOutOfRangeException>(
            () => SignedDistanceGrid.SampleNear(bvh, new Vec3(-12, -12, -12), new Vec3(12, 12, 12), cell: 1, reach: 1));
    }

    [Fact]
    public void Only_a_closed_consistently_wound_mesh_is_sampled_near_its_surface()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 16);

        // Carrying a side along a row is sound when the surface separates inside from outside.
        // With a face missing or one wound backwards it does not, and such a mesh is measured at
        // every node as it always was.
        var holed = Mesh(sphere.Vertices, sphere.Triangles[..^3]);
        var backwards = Mesh(sphere.Vertices, [sphere.Triangles[1], sphere.Triangles[0], .. sphere.Triangles[2..]]);

        Check.True(OffsetSmoothHandler.CanSampleNearSurface(sphere));
        Check.True(OffsetSmoothHandler.CanSampleNearSurface(Slotted()));
        Check.False(OffsetSmoothHandler.CanSampleNearSurface(holed));
        Check.False(OffsetSmoothHandler.CanSampleNearSurface(backwards));
    }

    [Fact]
    public void Closing_through_the_band_gives_the_grid_that_measuring_every_node_gives()
    {
        // The grid is what the closing computes and what the mesher is handed, so it is where
        // "the same result" can be said exactly: every node, to the last bit, however many cores
        // sampled it.
        //
        // The mesh made from it cannot be held to that. Manifold meshes a level set on its own
        // threads and numbers vertices and triangles in the order they finish, so two meshings of
        // one grid can list the same surface differently - which is what this test found when it
        // compared meshes, on twelve threads, and never on one.
        foreach (var (mesh, distance, iterations, cellSize) in Cases())
        {
            var plan = OffsetSmoothHandler.GridFor(mesh, distance, cellSize).Value;

            var expected = new OffsetSmoothHandler(GridSampling.EveryNode).Close(mesh, plan, distance, iterations).Values;
            var actual = new OffsetSmoothHandler(GridSampling.NearSurface).Close(mesh, plan, distance, iterations).Values;

            Check.Greater(expected.Length, 1000);
            Check.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected)
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(actual)));
        }
    }

    [Fact]
    public void Closing_through_the_band_gives_a_solid_of_the_same_size_and_soundness()
    {
        // What can be asked of the meshes without asking the mesher to repeat itself: the same
        // volume and area to well inside anything a caller could see, and both watertight.
        foreach (var (mesh, distance, iterations, cellSize) in Cases())
        {
            var request = new OffsetSmoothRequest(mesh, distance, iterations, cellSize);
            var expected = new OffsetSmoothHandler(GridSampling.EveryNode).Handle(request).Value;
            var actual = new OffsetSmoothHandler(GridSampling.NearSurface).Handle(request).Value;

            var (before, after) = (Statistics(expected), Statistics(actual));
            Check.RelativelyClose(before.Volume, after.Volume, 1e-6);
            Check.RelativelyClose(before.SurfaceArea, after.SurfaceArea, 1e-6);
            Check.True(Fixtures.TopologyOf(actual).IsWatertight);
        }
    }

    [Fact]
    public void The_engine_closes_through_the_band()
    {
        Check.Equal(GridSampling.NearSurface, OffsetSmoothHandler.Default);

        // And what it hands out is the solid the exact sampling makes.
        var slotted = Slotted();
        var exact = new OffsetSmoothHandler(GridSampling.EveryNode).Handle(new OffsetSmoothRequest(slotted, 3, 1, 0)).Value;
        var closed = Fixtures.Engine.Modifiers.OffsetSmooth(slotted, distance: 3, iterations: 1).Value;

        Check.RelativelyClose(Statistics(exact).Volume, Statistics(closed).Volume, 1e-6);
    }

    [Fact]
    public void Closing_and_counting_self_intersections_use_the_meshs_own_index()
    {
        // Each used to build an index of its own and throw it away. Asked for the mesh's, the
        // next query on that mesh - a decal, a deviation, another closing - finds it built.
        var closed = Mesh(Slotted().Vertices, Slotted().Triangles);
        _ = Fixtures.Engine.Modifiers.OffsetSmooth(closed, distance: 3, iterations: 1).Value;
        _ = ((ImmutableMesh)closed).Measurements.Index(() => throw new InvalidOperationException("closing did not keep the index"));

        var counted = Mesh(Slotted().Vertices, Slotted().Triangles);
        _ = Fixtures.Engine.Evaluators.CountSelfIntersections(counted).Value;
        _ = ((ImmutableMesh)counted).Measurements.Index(() => throw new InvalidOperationException("counting did not keep the index"));
    }

    private static (IMesh Mesh, double Distance, int Iterations, double CellSize)[] Cases() =>
    [
        (Slotted(), 3, 1, 0),
        (Slotted(), 3, 3, 0),
        (Slotted(), 1.5, 2, 0.75),
        (Fixtures.Sphere(Vec3.Zero, 10, 48), 2, 1, 0),
        (Shell(), 1.5, 2, 0.75),
        (Assets.LoadBench("ear_bolus.stl"), 2, 1, 0),
        (Assets.LoadBench("chin_bolus.stl"), 3, 2, 0),
    ];

    private static MeshStatistics Statistics(IMesh mesh) => Fixtures.Engine.Evaluators.GetStatistics(mesh).Value;

    private static IMesh Mesh(ImmutableArray<Vec3> vertices, ImmutableArray<int> triangles) =>
        Fixtures.Engine.CreateMesh(vertices, triangles, MeshMetadata.Named("sampled")).Value;

    /// <summary>A 20 mm cube with a 2 mm slot cut half way into one face.</summary>
    private static IMesh Slotted() => Fixtures.Engine.Booleans.Subtract(
        Fixtures.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 20)),
        Fixtures.Box(new Vec3(9, -1, 10), new Vec3(11, 21, 21))).Value;

    /// <summary>A 14 mm sphere with a 4 mm hollow at its centre: two surfaces, the inner facing inwards.</summary>
    private static IMesh Shell() => Fixtures.Engine.Booleans.Subtract(
        Fixtures.Sphere(new Vec3(5, 5, 5), 14, 32),
        Fixtures.Sphere(new Vec3(5, 5, 5), 4, 24)).Value;
}
