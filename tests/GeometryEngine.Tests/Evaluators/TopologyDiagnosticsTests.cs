namespace GeometryEngine.Tests.Evaluators;

[Suite("Evaluators / topology diagnostics")]
public sealed class TopologyDiagnosticsTests
{
    private static IMesh Mesh(ImmutableArray<Vec3> vertices, params int[] triangles) =>
        Fixtures.Engine.CreateMesh(vertices, [.. triangles], MeshMetadata.Named("diag")).Value;

    [Fact]
    public void A_clean_solid_has_consistent_winding_no_duplicate_faces_and_one_shell()
    {
        var topology = Fixtures.TopologyOf(Fixtures.UnitCube());

        Check.Equal(0, topology.InconsistentWindingEdgeCount);
        Check.Equal(0, topology.DuplicateFaceCount);
        Check.Equal(1, topology.ShellCount);
        Check.True(topology.IsClean);
    }

    [Fact]
    public void Two_disjoint_pieces_count_as_two_shells()
    {
        var vertices = ImmutableArray.Create(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0),          // piece 1
            new Vec3(10, 0, 0), new Vec3(11, 0, 0), new Vec3(10, 1, 0));      // piece 2, far away

        var topology = Fixtures.TopologyOf(Mesh(vertices, 0, 1, 2, 3, 4, 5));

        Check.Equal(2, topology.ShellCount);
    }

    [Fact]
    public void A_face_wound_the_wrong_way_shows_up_as_inconsistent_winding()
    {
        // Both triangles traverse the edge 0->1 in the same direction, so one of them
        // is wound backwards relative to its neighbour.
        var vertices = ImmutableArray.Create(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, -1, 0));

        var topology = Fixtures.TopologyOf(Mesh(vertices, 0, 1, 2, 0, 1, 3));

        Check.True(topology.InconsistentWindingEdgeCount >= 1);
    }

    [Fact]
    public void A_repeated_face_shows_up_as_a_duplicate()
    {
        var vertices = ImmutableArray.Create(
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));

        var topology = Fixtures.TopologyOf(Mesh(vertices, 0, 1, 2, 0, 1, 2));

        Check.Equal(1, topology.DuplicateFaceCount);
    }
}
