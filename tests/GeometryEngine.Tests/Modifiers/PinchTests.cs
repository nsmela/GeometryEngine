namespace GeometryEngine.Tests.Modifiers;

/// <summary>
/// A pinch is two distinct vertices at one position, where a surface touches itself at a point.
/// A level-set mesher emits them, they are manifold - every edge still carries two faces - and
/// welding on position alone destroys that. These pin down that it no longer does, and that
/// nothing leaves the decimator with two vertices in one place, because a 32-bit STL cannot
/// write such a pair down and the reader on the other side fuses them right back.
/// </summary>
[Suite("Modifiers / pinched surfaces")]
public sealed class PinchTests
{
    /// <summary>
    /// Two cubes meeting along one edge: closed, edge-manifold, and holding two coincident
    /// vertex pairs. Welding on position fuses each pair, and the shared edge - present in both
    /// cubes - then carries four faces.
    /// </summary>
    private static IMesh TouchingCubes()
    {
        var left = Fixtures.Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        var right = Fixtures.Box(new Vec3(1, 1, 0), new Vec3(2, 2, 1));

        var vertices = ImmutableArray.CreateBuilder<Vec3>();
        var triangles = ImmutableArray.CreateBuilder<int>();
        foreach (var piece in new[] { left, right })
        {
            var offset = vertices.Count;
            vertices.AddRange(piece.Vertices);
            foreach (var index in piece.Triangles)
            {
                triangles.Add(index + offset);
            }
        }

        return ImmutableMesh.Create(vertices.ToImmutable(), triangles.ToImmutable(), MeshMetadata.Named("touching cubes")).Value;
    }

    [Fact]
    public void The_fixture_starts_manifold_with_two_vertices_in_each_of_two_places()
    {
        var pinched = TouchingCubes();
        var topology = Fixtures.TopologyOf(pinched);

        Check.True(topology.IsWatertight);
        Check.Equal(0, topology.NonManifoldEdgeCount);
        Check.Equal(2, topology.DuplicateVertexCount);
    }

    [Fact]
    public void Decimating_a_pinched_surface_leaves_it_manifold()
    {
        var reduced = Fixtures.Engine.Modifiers.Decimate(TouchingCubes(), 24).Value;
        var topology = Fixtures.TopologyOf(reduced);

        Check.Equal(0, topology.NonManifoldEdgeCount);
        Check.True(topology.IsWatertight);
        Check.True(topology.IsConsistentlyWound);
    }

    [Fact]
    public void Decimating_a_pinched_surface_eases_the_coincident_vertices_apart()
    {
        var reduced = Fixtures.Engine.Modifiers.Decimate(TouchingCubes(), 24).Value;

        Check.Equal(0, Fixtures.TopologyOf(reduced).DuplicateVertexCount);
    }

    [Fact]
    public void Repairing_a_pinched_surface_leaves_it_manifold()
    {
        var repaired = Fixtures.Engine.Modifiers.Repair(TouchingCubes()).Value;

        Check.Equal(0, Fixtures.TopologyOf(repaired).NonManifoldEdgeCount);
    }

    [Fact]
    public void Easing_the_pinch_apart_moves_nothing_a_printer_could_see()
    {
        var pinched = TouchingCubes();

        var reduced = Fixtures.Engine.Modifiers.Decimate(pinched, 24).Value;

        // Two unit cubes, so the step is four weld tolerances on a bounding diagonal of about
        // 3 mm: under a millionth of a millimetre, against a 2 mm³ solid.
        Check.RelativelyClose(Fixtures.VolumeOf(pinched), Fixtures.VolumeOf(reduced), 1e-4);
    }

    [Fact]
    public void A_triangle_soup_is_still_welded_back_into_a_solid()
    {
        // The guard that spares a manifold mesh must not spare the input that needs welding.
        var soup = Assets.Unwelded(Fixtures.UnitCube());

        var repaired = Fixtures.Engine.Modifiers.Repair(soup).Value;

        Check.Equal(8, repaired.VertexCount);
        Check.True(Fixtures.TopologyOf(repaired).IsWatertight);
    }
}
