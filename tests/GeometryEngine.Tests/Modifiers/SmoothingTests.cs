using System.Collections.Immutable;
using GeometryEngine.Internal.Smoothing;

namespace GeometryEngine.Tests.Modifiers;

[Suite("Modifiers / smoothing")]
public sealed class LaplacianSmoothTests
{
    [Fact]
    public void Smoothing_a_roughened_sphere_returns_it_towards_a_sphere()
    {
        var rough = Roughened(Fixtures.Sphere(Vec3.Zero, 10, 48), radius: 10, amplitude: 0.6);
        var before = Roughness(rough, 10);

        var smoothed = Fixtures.Engine.Modifiers.LaplacianSmooth(rough, iterations: 10, strength: 0.5);

        Check.True(smoothed.IsSuccess);
        Check.Less(Roughness(smoothed.Value, 10), before * 0.5);
    }

    [Fact]
    public void Smoothing_keeps_the_volume_it_started_with()
    {
        // The reason for Taubin's second pass. Plain Laplacian smoothing has gain below one at
        // every non-zero frequency, so ten passes of it would visibly deflate a closed solid.
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 48);
        var before = Fixtures.VolumeOf(sphere);

        var smoothed = Fixtures.Engine.Modifiers.LaplacianSmooth(sphere, iterations: 10, strength: 0.5).Value;

        Check.RelativelyClose(before, Fixtures.VolumeOf(smoothed), 0.02);
    }

    [Fact]
    public void Smoothing_leaves_connectivity_alone()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 32);

        var smoothed = Fixtures.Engine.Modifiers.LaplacianSmooth(sphere, iterations: 4, strength: 0.5).Value;

        Check.Equal(sphere.VertexCount, smoothed.VertexCount);
        Check.Equal(sphere.TriangleCount, smoothed.TriangleCount);
        Check.True(Fixtures.TopologyOf(smoothed).IsWatertight);
    }

    [Fact]
    public void An_open_rim_is_held_still()
    {
        // Averaging a rim vertex against its neighbours drags the rim inwards, so smoothing a
        // torn scan would widen the tear. Boundary vertices are pinned instead.
        var open = OpenSquare();
        var rim = open.Vertices[0];

        var smoothed = Fixtures.Engine.Modifiers.LaplacianSmooth(open, iterations: 20, strength: 0.5).Value;

        Check.Close(rim.X, smoothed.Vertices[0].X, 1e-12);
        Check.Close(rim.Y, smoothed.Vertices[0].Y, 1e-12);
        Check.Close(rim.Z, smoothed.Vertices[0].Z, 1e-12);
    }

    [Fact]
    public void No_iterations_changes_no_geometry()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 32);

        var smoothed = Fixtures.Engine.Modifiers.LaplacianSmooth(sphere, iterations: 0, strength: 0.5).Value;

        Check.RelativelyClose(Fixtures.VolumeOf(sphere), Fixtures.VolumeOf(smoothed), 1e-12);
    }

    [Fact]
    public void A_strength_outside_the_pass_band_is_refused()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 16);

        Check.Equal("Modifiers.StrengthOutOfRange", Fixtures.Engine.Modifiers.LaplacianSmooth(sphere, 1, 0).Error.Code);
        Check.Equal("Modifiers.StrengthOutOfRange", Fixtures.Engine.Modifiers.LaplacianSmooth(sphere, 1, 1.5).Error.Code);
        Check.Equal("Modifiers.StrengthOutOfRange", Fixtures.Engine.Modifiers.LaplacianSmooth(sphere, 1, -0.5).Error.Code);
    }

    [Fact]
    public void A_negative_iteration_count_is_refused()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 16);

        Check.Equal("Modifiers.NegativeIterations", Fixtures.Engine.Modifiers.LaplacianSmooth(sphere, -1, 0.5).Error.Code);
    }

    [Fact]
    public void A_smoothed_mesh_records_what_made_it()
    {
        var smoothed = Fixtures.Engine.Modifiers.LaplacianSmooth(Fixtures.Sphere(Vec3.Zero, 10, 16)).Value;

        Check.Equal("GeometryEngine.Modifiers.LaplacianSmooth", smoothed.Metadata.CreatedBy);
    }

    /// <summary>Pushes every vertex in or out along its radius, alternately, to make a rough sphere.</summary>
    private static IMesh Roughened(IMesh sphere, double radius, double amplitude)
    {
        var vertices = new Vec3[sphere.VertexCount];
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = sphere.Vertices[i];
            var length = v.Length;
            var scale = length > 0 ? (radius + (i % 2 == 0 ? amplitude : -amplitude)) / length : 1;
            vertices[i] = v * scale;
        }

        return ImmutableMesh.Create([.. vertices], sphere.Triangles, sphere.Metadata).Value;
    }

    /// <summary>How far the worst vertex sits from the sphere it should be on.</summary>
    private static double Roughness(IMesh mesh, double radius)
    {
        var worst = 0.0;
        foreach (var vertex in mesh.Vertices)
        {
            worst = Math.Max(worst, Math.Abs(vertex.Length - radius));
        }

        return worst;
    }

    /// <summary>
    /// A flat sheet of four triangles around a raised centre vertex. Every vertex but the centre
    /// is on the rim, so it isolates what happens to a boundary.
    /// </summary>
    private static IMesh OpenSquare()
    {
        ImmutableArray<Vec3> vertices =
        [
            new(0, 0, 0),
            new(2, 0, 0),
            new(2, 2, 0),
            new(0, 2, 0),
            new(1, 1, 1),
        ];

        ImmutableArray<int> triangles = [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4];

        return ImmutableMesh.Create(vertices, triangles, MeshMetadata.Named("open square")).Value;
    }
}

[Suite("Modifiers / offset smoothing")]
public sealed class OffsetSmoothTests
{
    [Fact]
    public void Closing_fills_a_slot_narrower_than_the_distance()
    {
        // The point of the operation. The slot is 2 wide, so inflating by 3 closes it over and
        // deflating cannot reopen it.
        var slotted = Slotted();
        var before = Fixtures.VolumeOf(slotted);

        var closed = Fixtures.Engine.Modifiers.OffsetSmooth(slotted, distance: 3, iterations: 1);

        Check.True(closed.IsSuccess);
        Check.Greater(Fixtures.VolumeOf(closed.Value), before * 1.02);
        Check.True(Fixtures.TopologyOf(closed.Value).IsWatertight);
    }

    [Fact]
    public void Closing_leaves_a_shape_with_nothing_to_fill_about_where_it_was()
    {
        // A sphere has no concavity, so a closing should return it near enough unchanged - the
        // difference is what the sampling grid costs, and it is paid once however many rounds run.
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 48);
        var before = Fixtures.VolumeOf(sphere);

        var closed = Fixtures.Engine.Modifiers.OffsetSmooth(sphere, distance: 2, iterations: 1).Value;

        Check.RelativelyClose(before, Fixtures.VolumeOf(closed), 0.05);
    }

    [Fact]
    public void Iterating_does_not_run_away()
    {
        // Each round re-inflates and re-deflates the same distance. Once the concavities are
        // gone there is nothing left to fill, so the volume has to settle rather than climb.
        var slotted = Slotted();

        var once = Fixtures.Engine.Modifiers.OffsetSmooth(slotted, distance: 3, iterations: 1).Value;
        var thrice = Fixtures.Engine.Modifiers.OffsetSmooth(slotted, distance: 3, iterations: 3).Value;

        Check.RelativelyClose(Fixtures.VolumeOf(once), Fixtures.VolumeOf(thrice), 0.1);
        Check.True(Fixtures.TopologyOf(thrice).IsWatertight);
    }

    [Fact]
    public void It_agrees_with_the_re_meshing_route_it_replaces()
    {
        // Same arithmetic, different bookkeeping: DoubleOffset re-meshes between the two halves
        // of the cycle and this does not. One round of each should land in the same place, which
        // is the check that the grid implementation is doing what it claims.
        var slotted = Slotted();

        var viaGrid = Fixtures.Engine.Modifiers.OffsetSmooth(slotted, distance: 3, iterations: 1).Value;
        var viaRemesh = Fixtures.Engine.Modifiers.DoubleOffset(slotted, distance: 3, iterations: 1).Value;

        Check.RelativelyClose(Fixtures.VolumeOf(viaRemesh), Fixtures.VolumeOf(viaGrid), 0.1);
    }

    [Fact]
    public void No_iterations_returns_the_mesh_untouched()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 32);

        var closed = Fixtures.Engine.Modifiers.OffsetSmooth(sphere, distance: 2, iterations: 0).Value;

        Check.Equal(sphere.TriangleCount, closed.TriangleCount);
        Check.Equal("GeometryEngine.Modifiers.OffsetSmooth", closed.Metadata.CreatedBy);
    }

    [Fact]
    public void A_distance_finer_than_the_grid_is_refused()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 32);

        var closed = Fixtures.Engine.Modifiers.OffsetSmooth(sphere, distance: 0.1, iterations: 1, cellSize: 1);

        Check.Equal("Modifiers.DistanceBelowCell", closed.Error.Code);
    }

    [Fact]
    public void A_negative_iteration_count_is_refused()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 16);

        Check.Equal(
            "Modifiers.NegativeIterations",
            Fixtures.Engine.Modifiers.OffsetSmooth(sphere, 2, -1).Error.Code);
    }

    [Fact]
    public void A_closed_mesh_records_what_made_it()
    {
        var closed = Fixtures.Engine.Modifiers.OffsetSmooth(Fixtures.Sphere(Vec3.Zero, 10, 32), 2).Value;

        Check.Equal("GeometryEngine.Modifiers.OffsetSmooth", closed.Metadata.CreatedBy);
    }

    /// <summary>A 20 mm cube with a 2 mm slot cut half way into one face.</summary>
    private static IMesh Slotted()
    {
        var cube = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 20));
        var slot = Fixtures.Box(new Vec3(9, -1, 10), new Vec3(11, 21, 21));

        return Fixtures.Engine.Booleans.Subtract(cube, slot).Value;
    }
}

[Suite("Modifiers / edge smoothing")]
public sealed class SmoothEdgesTests
{
    [Fact]
    public void Flat_surface_is_preserved_exactly_and_costs_no_triangles()
    {
        // The half of this operation that behaves as advertised. A cube is six planar faces and
        // twelve 90-degree edges; below that angle there is no crease to act on, and a planar
        // patch needs no subdivision to sit within tolerance, so nothing at all should happen.
        var cube = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 20));

        var smoothed = Fixtures.Engine.Modifiers.SmoothEdges(cube, keepSharperThan: 60);

        Check.True(smoothed.IsSuccess);
        Check.Equal(cube.TriangleCount, smoothed.Value.TriangleCount);
        Check.Close(Fixtures.VolumeOf(cube), Fixtures.VolumeOf(smoothed.Value), 1e-9);
    }

    [Fact]
    public void Smoothing_past_a_models_real_edges_inflates_it()
    {
        // The half that does not. Raised above the cube's 90-degree edges, the interpolated
        // surface is free to swing out between the few vertices it has to pass through, and the
        // cube balloons. Pinned as a test because it is the failure mode a caller has to know
        // about, and a silent 168% volume gain on a clinical model is not acceptable to discover
        // in the field.
        var cube = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 20));

        var smoothed = Fixtures.Engine.Modifiers.SmoothEdges(cube, keepSharperThan: 120).Value;

        Check.Greater(Fixtures.VolumeOf(smoothed), Fixtures.VolumeOf(cube) * 2);
    }

    [Fact]
    public void A_smooth_organic_mesh_is_refined_gently()
    {
        // The case it suits: nothing sharp to fall off, so the surface is subdivided and barely
        // moved.
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 32);

        var smoothed = Fixtures.Engine.Modifiers.SmoothEdges(sphere, keepSharperThan: 30).Value;

        Check.Greater(smoothed.TriangleCount, sphere.TriangleCount);
        Check.RelativelyClose(Fixtures.VolumeOf(sphere), Fixtures.VolumeOf(smoothed), 0.02);
        Check.True(Fixtures.TopologyOf(smoothed).IsWatertight);
    }

    [Fact]
    public void A_negative_angle_or_tolerance_is_refused()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 16);

        Check.Equal("Modifiers.NegativeParameter", Fixtures.Engine.Modifiers.SmoothEdges(sphere, -1).Error.Code);
        Check.Equal("Modifiers.NegativeParameter", Fixtures.Engine.Modifiers.SmoothEdges(sphere, 30, -1).Error.Code);
    }

    [Fact]
    public void A_smoothed_mesh_records_what_made_it()
    {
        var smoothed = Fixtures.Engine.Modifiers.SmoothEdges(Fixtures.Sphere(Vec3.Zero, 10, 16)).Value;

        Check.Equal("GeometryEngine.Modifiers.SmoothEdges", smoothed.Metadata.CreatedBy);
    }
}

[Suite("Modifiers / crease smoothing")]
public sealed class SmoothCreasesTests
{
    [Fact]
    public void Surface_away_from_a_crease_is_bit_identical()
    {
        // The claim the operation exists to make. A sphere with a slice taken off it has a sharp
        // rim where the cut meets the curve, and a large curved area whose facets fold by only a
        // few degrees. The curved part must come back untouched - not nearly unchanged, exactly
        // unchanged - while the rim moves.
        //
        // A cube cannot show this: every one of its eight vertices is a corner on a 90-degree
        // edge, so a gate at 30 degrees legitimately marks all of them and there is no interior
        // to preserve. The fixture needs tessellated surface away from the crease.
        var sliced = Sliced();

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(sliced, roundSharperThan: 30, maxDeviation: 1.0);

        Check.True(rounded.IsSuccess);
        Check.Equal(sliced.VertexCount, rounded.Value.VertexCount);
        Check.Equal(sliced.TriangleCount, rounded.Value.TriangleCount);

        var untouched = 0;
        for (var v = 0; v < sliced.VertexCount; v++)
        {
            if (sliced.Vertices[v] == rounded.Value.Vertices[v])
            {
                untouched++;
            }
        }

        Check.Greater(untouched, sliced.VertexCount * 0.5);
        Check.Less(untouched, sliced.VertexCount);
    }

    [Fact]
    public void No_vertex_leaves_the_deviation_band()
    {
        // The bound is enforced, not hoped for: many iterations must not walk the surface out of
        // the band one pass at a time.
        var slotted = Slotted();
        using var index = Fixtures.Engine.Spatial.BuildIndex(slotted).Value;

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(
            slotted, roundSharperThan: 30, maxDeviation: 0.25, iterations: 40).Value;

        var worst = 0.0;
        foreach (var distance in index.SignedDistances(rounded.Vertices))
        {
            worst = Math.Max(worst, Math.Abs(distance));
        }

        // A shade over the band for the closest-point query's own tolerance, not a whole cell.
        Check.LessOrEqual(worst, 0.2501);
    }

    [Fact]
    public void A_tighter_band_moves_the_surface_less()
    {
        var slotted = Slotted();
        using var index = Fixtures.Engine.Spatial.BuildIndex(slotted).Value;

        var loose = Fixtures.Engine.Modifiers.SmoothCreases(slotted, 30, maxDeviation: 1.0, iterations: 20).Value;
        var tight = Fixtures.Engine.Modifiers.SmoothCreases(slotted, 30, maxDeviation: 0.1, iterations: 20).Value;

        Check.Greater(Worst(index, loose), Worst(index, tight));
    }

    [Fact]
    public void A_mesh_with_no_crease_sharp_enough_comes_back_untouched()
    {
        // A sphere's facets fold by a couple of degrees, so at 30 nothing qualifies and the
        // operation must decline to do anything rather than fair the whole surface.
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 48);

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(sphere, roundSharperThan: 30).Value;

        for (var v = 0; v < sphere.VertexCount; v++)
        {
            Check.True(sphere.Vertices[v] == rounded.Vertices[v]);
        }
    }

    [Fact]
    public void No_vertex_travels_further_than_the_bound()
    {
        // The guarantee, stated the way it is enforced. A cube is the hardest case for it: all
        // eight vertices are corners on 90-degree edges, so every one is movable and there is no
        // interior holding anything back. Even then no vertex may exceed the bound.
        var cube = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 20));

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(
            cube, roundSharperThan: 30, maxDeviation: 0.5, iterations: 40).Value;

        for (var v = 0; v < cube.VertexCount; v++)
        {
            Check.LessOrEqual((rounded.Vertices[v] - cube.Vertices[v]).Length, 0.5 + 1e-9);
        }
    }

    [Fact]
    public void A_cube_is_not_inflated_the_way_SmoothEdges_inflates_it()
    {
        // SmoothEdges gains 168% of a cube's volume. Here the change has to stay within what a
        // half-millimetre displacement of eight corners can account for, which for a 20 mm cube
        // is about 9% - volume is a sharp lever on so coarse a shape, and the bound is on
        // displacement rather than on volume.
        var cube = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 20));

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(
            cube, roundSharperThan: 30, maxDeviation: 0.5, iterations: 20).Value;

        Check.RelativelyClose(Fixtures.VolumeOf(cube), Fixtures.VolumeOf(rounded), 0.10);
    }

    [Fact]
    public void The_crease_it_was_pointed_at_actually_softens()
    {
        // That it moves the right vertices is not the same as that it rounds them. Counting the
        // edges still folding past 40 degrees before and after says whether the fold itself. The rim
        // of a sphere sliced above its equator folds by about 53 degrees, so 40 is the probe that
        // sees it and 60 sees nothing.
        // relaxed.
        var sliced = Sliced();

        MeshAdjacency.FindCreaseVertices(sliced, 40, out var before);

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(
            sliced, roundSharperThan: 30, maxDeviation: 0.5, iterations: 20).Value;

        MeshAdjacency.FindCreaseVertices(rounded, 40, out var after);

        Check.Greater(before, 0);
        Check.Less(after, before);
    }

    [Fact]
    public void Connectivity_and_watertightness_survive()
    {
        var slotted = Slotted();

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(slotted, 30, 0.5, 10).Value;

        Check.Equal(slotted.TriangleCount, rounded.TriangleCount);
        Check.True(Fixtures.TopologyOf(rounded).IsWatertight);
    }

    [Fact]
    public void An_open_rim_is_held_still()
    {
        var open = OpenSheet();
        var rim = open.Vertices[0];

        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(open, 10, 1.0, 20).Value;

        Check.Close(rim.X, rounded.Vertices[0].X, 1e-12);
        Check.Close(rim.Y, rounded.Vertices[0].Y, 1e-12);
        Check.Close(rim.Z, rounded.Vertices[0].Z, 1e-12);
    }

    [Fact]
    public void Bad_parameters_are_refused()
    {
        var cube = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10));

        Check.Equal("Modifiers.NegativeParameter", Fixtures.Engine.Modifiers.SmoothCreases(cube, -1).Error.Code);
        Check.Equal("Modifiers.NegativeParameter", Fixtures.Engine.Modifiers.SmoothCreases(cube, 30, -1).Error.Code);
        Check.Equal("Modifiers.NegativeIterations", Fixtures.Engine.Modifiers.SmoothCreases(cube, 30, 1, -1).Error.Code);
        Check.Equal("Modifiers.StrengthOutOfRange", Fixtures.Engine.Modifiers.SmoothCreases(cube, 30, 1, 1, 0).Error.Code);
    }

    [Fact]
    public void A_rounded_mesh_records_what_made_it()
    {
        var rounded = Fixtures.Engine.Modifiers.SmoothCreases(Fixtures.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10))).Value;

        Check.Equal("GeometryEngine.Modifiers.SmoothCreases", rounded.Metadata.CreatedBy);
    }

    private static double Worst(ISpatialIndex index, IMesh mesh)
    {
        var worst = 0.0;
        foreach (var distance in index.SignedDistances(mesh.Vertices))
        {
            worst = Math.Max(worst, Math.Abs(distance));
        }

        return worst;
    }

    /// <summary>
    /// A sphere with a slice cut off: a sharp rim where the cut plane meets the curve, and a
    /// large tessellated curved area whose folds are far too shallow to qualify.
    /// </summary>
    private static IMesh Sliced()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 48);
        var knife = Fixtures.Box(new Vec3(-12, -12, 6), new Vec3(12, 12, 12));

        return Fixtures.Engine.Booleans.Subtract(sphere, knife).Value;
    }

    /// <summary>A 20 mm cube with a 2 mm slot cut half way into one face - creases along the slot.</summary>
    private static IMesh Slotted()
    {
        var cube = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(20, 20, 20));
        var slot = Fixtures.Box(new Vec3(9, -1, 10), new Vec3(11, 21, 21));

        return Fixtures.Engine.Booleans.Subtract(cube, slot).Value;
    }

    /// <summary>A folded open sheet: a crease down the middle and a rim all round.</summary>
    private static IMesh OpenSheet()
    {
        ImmutableArray<Vec3> vertices =
        [
            new(0, 0, 0),
            new(0, 4, 0),
            new(2, 0, 1),
            new(2, 4, 1),
            new(4, 0, 0),
            new(4, 4, 0),
        ];

        ImmutableArray<int> triangles = [0, 2, 1, 1, 2, 3, 2, 4, 3, 3, 4, 5];

        return ImmutableMesh.Create(vertices, triangles, MeshMetadata.Named("folded sheet")).Value;
    }
}
