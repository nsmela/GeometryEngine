using System.Collections.Immutable;

namespace GeometryEngine.Internal.Smoothing;

/// <summary>
/// Connectivity the vertex filters need: who neighbours whom, which vertices sit on an open edge,
/// and how sharply the surface folds along each edge. Built from the triangle list alone.
/// </summary>
internal static class MeshAdjacency
{
    /// <summary>
    /// Vertex-to-vertex adjacency in compressed row form: the neighbours of v are the entries of
    /// <c>Neighbours</c> between <c>Start[v]</c> and <c>Start[v + 1]</c>.
    ///
    /// A neighbour appears once per incident triangle, so a vertex sees a shared neighbour twice
    /// and it is weighted accordingly. That is the correct umbrella weight for a triangle mesh,
    /// where each edge contributes once from either side.
    /// </summary>
    public static (int[] Start, int[] Neighbours) Build(int vertexCount, ImmutableArray<int> triangles)
    {
        var degrees = new int[vertexCount];
        for (var t = 0; t < triangles.Length; t += 3)
        {
            degrees[triangles[t]] += 2;
            degrees[triangles[t + 1]] += 2;
            degrees[triangles[t + 2]] += 2;
        }

        var start = new int[vertexCount + 1];
        for (var v = 0; v < vertexCount; v++)
        {
            start[v + 1] = start[v] + degrees[v];
        }

        var neighbours = new int[start[vertexCount]];
        var cursor = new int[vertexCount];
        Array.Copy(start, cursor, vertexCount);

        for (var t = 0; t < triangles.Length; t += 3)
        {
            var a = triangles[t];
            var b = triangles[t + 1];
            var c = triangles[t + 2];

            neighbours[cursor[a]++] = b;
            neighbours[cursor[a]++] = c;
            neighbours[cursor[b]++] = a;
            neighbours[cursor[b]++] = c;
            neighbours[cursor[c]++] = a;
            neighbours[cursor[c]++] = b;
        }

        return (start, neighbours);
    }

    /// <summary>
    /// Vertices on an open edge - one used by a single triangle. Every filter here holds them
    /// still: averaging a rim vertex against its neighbours pulls the rim inwards, so a torn scan
    /// would have its hole widened by the act of smoothing it.
    /// </summary>
    public static bool[] FindBoundaryVertices(int vertexCount, ImmutableArray<int> triangles)
    {
        var uses = new Dictionary<(int, int), int>();
        for (var t = 0; t < triangles.Length; t += 3)
        {
            Count(uses, triangles[t], triangles[t + 1]);
            Count(uses, triangles[t + 1], triangles[t + 2]);
            Count(uses, triangles[t + 2], triangles[t]);
        }

        var boundary = new bool[vertexCount];
        foreach (var ((a, b), count) in uses)
        {
            if (count == 1)
            {
                boundary[a] = true;
                boundary[b] = true;
            }
        }

        return boundary;
    }

    /// <summary>
    /// Vertices touching an edge where the surface folds by more than
    /// <paramref name="degrees"/> - the creases. The angle is measured from flat, so coplanar
    /// triangles read zero and a cube's edges read ninety, matching how Manifold states the same
    /// threshold.
    /// </summary>
    public static bool[] FindCreaseVertices(
        IMesh mesh, double degrees, out int creaseEdgeCount)
    {
        var triangles = mesh.Triangles;
        var normals = new Vec3[mesh.TriangleCount];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = mesh.Vertices[triangles[t * 3]];
            var b = mesh.Vertices[triangles[(t * 3) + 1]];
            var c = mesh.Vertices[triangles[(t * 3) + 2]];

            var normal = Cross(b - a, c - a);
            var length = normal.Length;
            normals[t] = length > 0 ? normal / length : Vec3.Zero;
        }

        // First triangle seen per edge, then the angle when the second arrives. An edge used by
        // more than two triangles is non-manifold; the first pair decides it, which is enough to
        // classify the vertices and avoids inventing an answer for a defect.
        var seen = new Dictionary<(int, int), int>();
        var crease = new bool[mesh.VertexCount];
        var threshold = Math.Cos(Math.Clamp(degrees, 0, 180) * Math.PI / 180);
        creaseEdgeCount = 0;

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                var u = triangles[(t * 3) + corner];
                var v = triangles[(t * 3) + ((corner + 1) % 3)];
                var key = u < v ? (u, v) : (v, u);

                if (!seen.TryGetValue(key, out var other))
                {
                    seen[key] = t;
                    continue;
                }

                // cos falls as the fold sharpens, so a fold past the threshold reads *below* it.
                if (Dot(normals[t], normals[other]) < threshold)
                {
                    crease[u] = true;
                    crease[v] = true;
                    creaseEdgeCount++;
                }
            }
        }

        return crease;
    }

    /// <summary>
    /// Grows a vertex set outwards by <paramref name="rings"/> steps of adjacency. A crease only
    /// marks the vertices on the fold itself, and moving those alone puts the whole correction
    /// into one ring of triangles - a kink rather than a blend. Widening the movable set is what
    /// gives the rounding somewhere to go.
    /// </summary>
    public static bool[] Dilate(bool[] set, int[] start, int[] neighbours, int rings)
    {
        var current = (bool[])set.Clone();

        for (var ring = 0; ring < rings; ring++)
        {
            var next = (bool[])current.Clone();
            for (var v = 0; v < current.Length; v++)
            {
                if (!current[v])
                {
                    continue;
                }

                for (var n = start[v]; n < start[v + 1]; n++)
                {
                    next[neighbours[n]] = true;
                }
            }

            current = next;
        }

        return current;
    }

    private static void Count(Dictionary<(int, int), int> uses, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        uses[key] = uses.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static Vec3 Cross(Vec3 u, Vec3 v) =>
        new((u.Y * v.Z) - (u.Z * v.Y), (u.Z * v.X) - (u.X * v.Z), (u.X * v.Y) - (u.Y * v.X));

    private static double Dot(Vec3 u, Vec3 v) => (u.X * v.X) + (u.Y * v.Y) + (u.Z * v.Z);
}
