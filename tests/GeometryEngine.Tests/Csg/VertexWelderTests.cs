using GeometryEngine.Internal.Csg;

namespace GeometryEngine.Tests.Csg;

/// <summary>
/// The welder decides which vertices of a mesh are one vertex, so every STL read, every healed
/// boolean result and every audit rests on it. These tests hold it, answer for answer, to the
/// welder written the plainest way: cells the size of the tolerance, all twenty-seven around a
/// vertex searched in order, and the first near neighbour found taken.
///
/// Which neighbour is taken matters as much as whether one is. Two kept vertices can both be
/// within the tolerance of a third while being further than it from each other, and the one
/// chosen decides the indices of the mesh that comes out.
/// </summary>
[Suite("Csg / vertex welder against the plain welder")]
public sealed class VertexWelderTests
{
    [Fact]
    public void Crowded_vertices_are_welded_to_the_same_neighbours_at_every_tolerance_and_distance()
    {
        var random = new Random(11);
        var severalNear = 0;

        foreach (var tolerance in new[] { 1e-9, 1e-3, 0.5 })
        {
            foreach (var origin in new[] { 0.0, 250.0, -1234.5 })
            {
                for (var trial = 0; trial < 25; trial++)
                {
                    // A lattice a little finer than the tolerance, each point nudged: some pairs
                    // inside it, some outside, and many vertices with more than one neighbour.
                    var vertices = Enumerable.Range(0, random.Next(3, 500))
                        .Select(_ => new Vec3(
                            origin + (((random.Next(9) * 0.7) + (random.NextDouble() * 0.5)) * tolerance),
                            origin + (((random.Next(9) * 0.7) + (random.NextDouble() * 0.5)) * tolerance),
                            origin + (((random.Next(5) * 0.7) + (random.NextDouble() * 0.5)) * tolerance)))
                        .ToArray();

                    severalNear += AssertWeldedAlike(vertices, tolerance);
                }
            }
        }

        // The case that tells a welder taking the right neighbour from one taking any.
        Check.Greater(severalNear, 1000);
    }

    [Fact]
    public void A_stream_of_triangle_corners_is_welded_into_the_same_mesh()
    {
        // What an STL holds: every triangle's corners written out, shared ones repeated.
        var sphere = Fixtures.Sphere(new Vec3(40, -15, 3), 25, 64);
        var corners = sphere.Triangles.Select(index => sphere.Vertices[index]).ToArray();

        _ = AssertWeldedAlike(corners, 1e-9);

        var welder = new VertexWelder(1e-9);
        foreach (var corner in corners)
        {
            _ = welder.AddOrGet(corner);
        }

        Check.Equal(sphere.VertexCount, welder.Vertices.Count);
    }

    [Fact]
    public void Vertices_on_the_faces_of_cells_are_welded_alike()
    {
        // Whole multiples and exact halves of the tolerance: every coordinate sits on the boundary
        // of a cell or at its middle, where "which side" is decided by a rounding.
        foreach (var tolerance in new[] { 1e-9, 0.125, 1.0 })
        {
            var vertices = (
                from x in Enumerable.Range(-4, 9)
                from y in Enumerable.Range(-4, 9)
                from z in Enumerable.Range(-2, 5)
                select new Vec3(x * 0.5 * tolerance, y * 0.5 * tolerance, z * 0.5 * tolerance)).ToArray();

            _ = AssertWeldedAlike(vertices, tolerance);
        }
    }

    [Fact]
    public void The_most_crowded_streams_are_welded_without_stalling()
    {
        const int Count = 300_000;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var heap = new VertexWelder(1e-9);
        for (var i = 0; i < Count; i++)
        {
            Check.Equal(0, heap.AddOrGet(new Vec3(1, 2, 3)));
        }

        // A tolerance and a bit apart: every vertex has near neighbours, none near enough.
        var line = new VertexWelder(1e-9);
        for (var i = 0; i < Count; i++)
        {
            Check.Equal(i, line.AddOrGet(new Vec3(i * 1.01e-9, 0, 0)));
        }

        // As many kept vertices as will fit in a few cells, each just clear of the last.
        var packed = new VertexWelder(1.0);
        for (var i = 0; i < Count; i++)
        {
            _ = packed.AddOrGet(new Vec3((i % 40) * 1.001, (i / 40 % 40) * 1.001, (i / 1600 % 40) * 1.001));
        }

        Check.Equal(64_000, packed.Vertices.Count);
        Check.Less(watch.Elapsed.TotalSeconds, 20);
    }

    /// <summary>Welds the stream both ways and returns how many vertices had several near neighbours.</summary>
    private static int AssertWeldedAlike(IReadOnlyList<Vec3> vertices, double tolerance)
    {
        var welder = new VertexWelder(tolerance);
        var plain = new PlainWelder(tolerance);
        var severalNear = 0;

        for (var i = 0; i < vertices.Count; i++)
        {
            severalNear += plain.CountNear(vertices[i]) > 1 ? 1 : 0;
            var expected = plain.AddOrGet(vertices[i]);
            var actual = welder.AddOrGet(vertices[i]);
            if (expected != actual)
            {
                Check.Equal($"vertex {i} of {vertices.Count} -> {expected}", $"vertex {i} of {vertices.Count} -> {actual}");
            }
        }

        Check.Equal(plain.Vertices.Count, welder.Vertices.Count);
        Check.True(plain.Vertices.SequenceEqual(welder.Vertices));
        return severalNear;
    }

    /// <summary>The welder as first written, kept here as the statement of what welding means.</summary>
    private sealed class PlainWelder(double tolerance)
    {
        private readonly Dictionary<(long, long, long), List<int>> _cells = [];

        public List<Vec3> Vertices { get; } = [];

        public int AddOrGet(Vec3 vertex)
        {
            foreach (var candidate in Near(vertex))
            {
                return candidate;
            }

            Vertices.Add(vertex);
            var home = CellOf(vertex);
            if (!_cells.TryGetValue(home, out var occupants))
            {
                _cells[home] = occupants = [];
            }

            occupants.Add(Vertices.Count - 1);
            return Vertices.Count - 1;
        }

        public int CountNear(Vec3 vertex) => Near(vertex).Count();

        private IEnumerable<int> Near(Vec3 vertex)
        {
            var (x, y, z) = CellOf(vertex);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!_cells.TryGetValue((x + dx, y + dy, z + dz), out var occupants))
                        {
                            continue;
                        }

                        foreach (var candidate in occupants)
                        {
                            if ((Vertices[candidate] - vertex).LengthSquared <= tolerance * tolerance)
                            {
                                yield return candidate;
                            }
                        }
                    }
                }
            }
        }

        private (long, long, long) CellOf(Vec3 vertex) => (
            (long)Math.Floor(vertex.X / tolerance),
            (long)Math.Floor(vertex.Y / tolerance),
            (long)Math.Floor(vertex.Z / tolerance));
    }
}
