namespace GeometryEngine.Tests.Evaluators;

/// <summary>
/// The topology audit, checked figure for figure against its definitions written the plainest
/// way there is: sets, a dictionary, and every pair of vertices compared. The audit may count
/// however it likes; it must count the same on anything, and the meshes here are made to be as
/// untidy as a mesh can be.
/// </summary>
[Suite("Evaluators / topology audit against its definitions")]
public sealed class TopologyTallyTests
{
    private static readonly double Tolerance = GeometryEngine.Core.Geometry.Primitives.Tolerance.Welding.Value;

    [Fact]
    public void Random_triangle_soups_are_audited_as_the_definitions_audit_them()
    {
        var random = new Random(20261005);

        for (var trial = 0; trial < 400; trial++)
        {
            // Few vertices and many triangles, so shared indices, doubled faces, edges met three
            // times, edges walked twice the same way, repeated corners and vertices nothing uses
            // all turn up, often in one mesh.
            var vertexCount = random.Next(4, 12);
            var triangleCount = random.Next(1, 40);
            var vertices = Enumerable.Range(0, vertexCount)
                .Select(_ => new Vec3(random.NextDouble() * 10, random.NextDouble() * 10, random.NextDouble() * 10))
                .ToImmutableArray();
            var triangles = Enumerable.Range(0, triangleCount * 3).Select(_ => random.Next(vertexCount)).ToImmutableArray();

            Check.Equal(Define(vertices, triangles), Audit(vertices, triangles));
        }
    }

    [Fact]
    public void A_closed_solid_and_one_with_a_face_taken_out_are_audited_as_defined()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 10, 64);
        var open = sphere.Triangles[..^3];

        Check.Equal(Define(sphere.Vertices, sphere.Triangles), Audit(sphere.Vertices, sphere.Triangles));
        Check.Equal(Define(sphere.Vertices, open), Audit(sphere.Vertices, open));
        Check.Equal(3, Audit(sphere.Vertices, open).Boundary);
    }

    [Fact]
    public void Vertices_crowded_to_within_the_tolerance_are_counted_as_every_pair_compared_counts_them()
    {
        var random = new Random(7);

        // Far from the origin as well as at it: the grid the audit sorts vertices into is the
        // size of the tolerance, and a millimetre-scale coordinate is a long way across it.
        foreach (var origin in new[] { 0.0, 250.0, -1234.5 })
        {
            for (var trial = 0; trial < 40; trial++)
            {
                // A lattice a little finer than the tolerance, each point nudged, so some pairs
                // are inside it, some outside, and chains of near neighbours run across cells.
                var vertices = Enumerable.Range(0, random.Next(3, 400))
                    .Select(_ => new Vec3(
                        origin + ((random.Next(8) * 0.7) + (random.NextDouble() * 0.5)) * Tolerance,
                        origin + ((random.Next(8) * 0.7) + (random.NextDouble() * 0.5)) * Tolerance,
                        origin + ((random.Next(4) * 0.7) + (random.NextDouble() * 0.5)) * Tolerance))
                    .ToImmutableArray();
                ImmutableArray<int> triangles = [0, 1, 2];

                Check.Equal(Define(vertices, triangles).DuplicateVertices, Audit(vertices, triangles).DuplicateVertices);
            }
        }
    }

    [Fact]
    public void The_meshes_least_like_a_surface_are_audited_without_stalling()
    {
        const int Count = 200_000;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Every triangle on one vertex: its edges all fall to be counted together.
        var rim = Enumerable.Range(0, Count).Select(i => new Vec3(Math.Cos(i), Math.Sin(i), i * 1e-3)).Prepend(Vec3.Zero);
        var fan = Audit([.. rim], [.. Enumerable.Range(0, Count).SelectMany(i => new[] { 0, 1 + i, 1 + ((i + 1) % Count) })]);
        Check.Equal(2 * Count, fan.Edges);
        Check.Equal(1, fan.Shells);

        // Every vertex in one place, and every face the same face.
        var heap = Audit([.. Enumerable.Repeat(new Vec3(1, 2, 3), Count)], [.. Enumerable.Repeat(new[] { 0, 1, 2 }, Count).SelectMany(t => t)]);
        Check.Equal(Count - 1, heap.DuplicateVertices);
        Check.Equal(Count - 1, heap.DuplicateFaces);
        Check.Equal(3, heap.NonManifold);

        // Vertices a tolerance and a bit apart along a line: every one has near neighbours and
        // none is a duplicate.
        var line = Audit([.. Enumerable.Range(0, Count).Select(i => new Vec3(i * 1.01 * Tolerance, 0, 0))], [0, 1, 2]);
        Check.Equal(0, line.DuplicateVertices);

        // Generous: the three together take well under a second. A pass that had gone quadratic
        // on any of them would take minutes.
        Check.Less(watch.Elapsed.TotalSeconds, 20);
    }

    private static Tally Audit(ImmutableArray<Vec3> vertices, ImmutableArray<int> triangles)
    {
        var mesh = Fixtures.Engine.CreateMesh(vertices, triangles, MeshMetadata.Named("audited")).Value;
        var audited = Fixtures.Engine.Evaluators.ValidateTopology(mesh).Value;
        return new Tally(
            audited.BoundaryEdgeCount,
            audited.NonManifoldEdgeCount,
            audited.InconsistentWindingEdgeCount,
            audited.DuplicateFaceCount,
            audited.EdgeCount,
            audited.DuplicateVertexCount,
            audited.UnreferencedVertexCount,
            audited.ShellCount);
    }

    private static Tally Define(ImmutableArray<Vec3> vertices, ImmutableArray<int> triangles)
    {
        var uses = new Dictionary<(int, int), int>();
        var walked = new HashSet<(int, int)>();
        var faces = new HashSet<(int, int, int)>();
        var referenced = new HashSet<int>();
        var shellOf = Enumerable.Range(0, vertices.Length).ToArray();
        var (inconsistent, duplicates) = (0, 0);

        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);

            // Every triangle joins its corners into one shell, whatever else is wrong with it.
            foreach (var corner in new[] { a, b, c })
            {
                referenced.Add(corner);
                var (from, into) = (shellOf[corner], shellOf[a]);
                for (var v = 0; v < shellOf.Length && from != into; v++)
                {
                    shellOf[v] = shellOf[v] == from ? into : shellOf[v];
                }
            }

            var corners = new[] { a, b, c };
            Array.Sort(corners);
            if (!faces.Add((corners[0], corners[1], corners[2])))
            {
                duplicates++;
            }

            // A triangle with a repeated corner has no edges to pair.
            if (a == b || b == c || c == a)
            {
                continue;
            }

            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
            {
                if (!walked.Add((from, to)))
                {
                    inconsistent++;
                }

                var edge = from < to ? (from, to) : (to, from);
                uses[edge] = uses.GetValueOrDefault(edge) + 1;
            }
        }

        // A vertex is a duplicate when one kept before it lies within the tolerance; one that is
        // a duplicate is not kept, so it cannot make duplicates of those after it.
        var kept = new List<Vec3>();
        var duplicateVertices = 0;
        foreach (var vertex in vertices)
        {
            if (kept.Any(other => (other - vertex).LengthSquared <= Tolerance * Tolerance))
            {
                duplicateVertices++;
            }
            else
            {
                kept.Add(vertex);
            }
        }

        return new Tally(
            uses.Values.Count(n => n == 1),
            uses.Values.Count(n => n > 2),
            inconsistent,
            duplicates,
            uses.Count,
            duplicateVertices,
            vertices.Length - referenced.Count,
            referenced.Select(v => shellOf[v]).Distinct().Count());
    }

    private readonly record struct Tally(
        int Boundary,
        int NonManifold,
        int InconsistentWinding,
        int DuplicateFaces,
        int Edges,
        int DuplicateVertices,
        int Unreferenced,
        int Shells);
}
