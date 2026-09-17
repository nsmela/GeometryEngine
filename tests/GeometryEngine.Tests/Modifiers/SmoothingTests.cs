using System.Collections.Immutable;

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
