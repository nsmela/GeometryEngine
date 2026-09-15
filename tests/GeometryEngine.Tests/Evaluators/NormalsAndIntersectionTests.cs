namespace GeometryEngine.Tests.Evaluators;

[Suite("Evaluators / normals, self-intersections, counts")]
public sealed class NormalsAndIntersectionTests
{
    [Fact]
    public void Every_vertex_normal_of_a_sphere_points_away_from_its_centre()
    {
        var centre = new Vec3(3, -2, 1);
        var sphere = Fixtures.Sphere(centre, 4, 48);

        var normals = Fixtures.Engine.Evaluators.ComputeVertexNormals(sphere).Value;

        Check.Equal(sphere.VertexCount, normals.Length);
        for (var i = 0; i < sphere.VertexCount; i++)
        {
            var radial = (sphere.Vertices[i] - centre) / 4;
            Check.Greater(normals[i].Dot(radial), 0.99);
            Check.Close(1, normals[i].Length, 1e-12);
        }
    }

    [Fact]
    public void A_vertex_normal_is_weighted_towards_the_larger_faces_around_it()
    {
        // One corner shared by a big triangle facing +z and a small one facing +x.
        var vertices = ImmutableArray.Create(
            new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(0, 10, 0),
            new Vec3(0, 1, 0), new Vec3(0, 0, -1));
        var mesh = Fixtures.Engine.CreateMesh(vertices, [0, 1, 2, 0, 3, 4], MeshMetadata.Named("corner")).Value;

        var normal = Fixtures.Engine.Evaluators.ComputeVertexNormals(mesh).Value[0];

        Check.Greater(normal.Z, 0.99);
    }

    [Fact]
    public void An_unused_vertex_gets_a_zero_normal()
    {
        var cube = Fixtures.UnitCube();
        var padded = Fixtures.Engine.CreateMesh(cube.Vertices.Add(new Vec3(5, 5, 5)), cube.Triangles, MeshMetadata.Named("padded")).Value;

        Check.Equal(Vec3.Zero, Fixtures.Engine.Evaluators.ComputeVertexNormals(padded).Value[^1]);
    }

    [Fact]
    public void A_clean_solid_has_no_self_intersections()
    {
        Check.Equal(0, Fixtures.Engine.Evaluators.CountSelfIntersections(Fixtures.Sphere(Vec3.Zero, 3, 48)).Value);
        Check.Equal(0, Fixtures.Engine.Evaluators.CountSelfIntersections(Assets.LoadBench("sphere.stl")).Value);
    }

    [Fact]
    public void Two_solids_passing_through_each_other_intersect()
    {
        var overlapping = Assets.Concatenate(Fixtures.Cube(0, 2), Fixtures.Cube(1, 2));

        var count = Fixtures.Engine.Evaluators.CountSelfIntersections(overlapping).Value;

        // Each cube pierces the other through three of its faces, two triangles apiece.
        Check.GreaterOrEqual(count, 6);
    }

    [Fact]
    public void Solids_that_only_touch_do_not_intersect()
    {
        var touching = Assets.Concatenate(Fixtures.Cube(0, 1), Fixtures.Box(new Vec3(1, 0, 0), new Vec3(2, 1, 1)));

        Check.Equal(0, Fixtures.Engine.Evaluators.CountSelfIntersections(touching).Value);
    }

    [Fact]
    public void A_sliver_in_a_closed_surface_is_degenerate_but_does_not_open_it()
    {
        // Split one cube edge at its midpoint on one side only, and close the T-junction with a
        // zero-area triangle - the shape a boolean leaves where a vertex lands on an edge.
        var cube = Fixtures.UnitCube();
        var triangles = cube.Triangles.ToList();

        // The first triangle, (a, b, c), becomes (a, m, c) and (m, b, c) with m the midpoint of
        // a-b; the sliver (a, b, m) then pairs with both halves and with the neighbour across a-b.
        var (a, b, c) = (triangles[0], triangles[1], triangles[2]);
        var mid = cube.VertexCount;
        var vertices = cube.Vertices.Add(cube.Vertices[a].LerpTo(cube.Vertices[b], 0.5));

        triangles.RemoveRange(0, 3);
        triangles.AddRange([a, mid, c, mid, b, c, a, b, mid]);

        var mesh = Fixtures.Engine.CreateMesh(vertices, [.. triangles], MeshMetadata.Named("sliver")).Value;
        var topology = Fixtures.TopologyOf(mesh);

        Check.Equal(1, topology.DegenerateTriangleCount);
        Check.True(topology.IsClosed);
        Check.True(topology.IsEdgeManifold);
        Check.Close(1, Fixtures.VolumeOf(mesh), 1e-12);
    }

    [Fact]
    public void A_cube_has_eighteen_edges_and_no_unused_vertices()
    {
        // Twelve edges of the cube plus one diagonal across each of its six faces.
        var topology = Fixtures.TopologyOf(Fixtures.UnitCube());

        Check.Equal(18, topology.EdgeCount);
        Check.Equal(0, topology.UnreferencedVertexCount);
    }
}
