using System.Collections.Immutable;

namespace GeometryEngine.Internal.Smoothing;

/// <summary>
/// Taubin's λ|μ surface fairing: moves every vertex towards the centroid of its neighbours to
/// reduce curvature, then back out again by a smaller negative factor to undo the shrinkage that
/// repeated averaging otherwise causes.
///
/// Plain Laplacian smoothing is a low-pass filter with gain below one everywhere except at zero
/// frequency, so each pass pulls the whole surface inwards - run it enough times on a closed
/// solid and it collapses towards a point. Taubin's second pass with μ negative restores unit
/// gain over a pass band, which keeps the volume near where it started while still attenuating
/// the high-frequency detail that reads as roughness. For a bolus that has to keep its fit
/// against skin, that distinction is the whole point.
///
/// Weights are uniform (the umbrella operator) rather than cotangent. Cotangent weights follow
/// the surface's true geometry and give better results on a well-conditioned mesh, but they go
/// negative on obtuse triangles and blow up on slivers - and a mesh arriving from a clinical
/// scanner is full of both. Uniform weights depend only on connectivity, so a degenerate
/// triangle cannot make the filter unstable.
/// </summary>
internal static class LaplacianSmoother
{
    /// <summary>
    /// Taubin's pass-band frequency. λ and μ satisfy 1/λ + 1/μ = k, which is what puts the
    /// filter's unit gain at k rather than only at zero and so preserves volume. 0.1 is the
    /// value Taubin's paper uses and behaves well across the range of strengths offered here.
    /// </summary>
    private const double PassBand = 0.1;

    /// <summary>
    /// Smooths in place over <paramref name="iterations"/> λ|μ pairs. Triangles are returned
    /// unchanged: this moves vertices and never alters connectivity, so a watertight input stays
    /// watertight and a torn one is not silently closed.
    /// </summary>
    /// <param name="strength">λ, the fraction of the way towards the neighbour centroid each pass moves a vertex.</param>
    public static (ImmutableArray<Vec3> Vertices, ImmutableArray<int> Triangles) Smooth(
        IMesh mesh, int iterations, double strength)
    {
        var positions = mesh.Vertices.ToArray();
        var triangles = mesh.Triangles;

        var (neighbourStart, neighbours) = BuildAdjacency(positions.Length, triangles);
        var pinned = FindBoundaryVertices(positions.Length, triangles);

        // μ is negative and slightly larger in magnitude than λ; see PassBand.
        var mu = 1.0 / (PassBand - (1.0 / strength));

        var buffer = new Vec3[positions.Length];
        for (var i = 0; i < iterations; i++)
        {
            Pass(positions, buffer, neighbourStart, neighbours, pinned, strength);
            Pass(positions, buffer, neighbourStart, neighbours, pinned, mu);
        }

        return ([.. positions], triangles);
    }

    /// <summary>
    /// One filter pass. The new positions are written to <paramref name="buffer"/> and only
    /// swapped in once every vertex has been computed, so each vertex sees the same generation of
    /// its neighbours. Updating in place instead would let a vertex read neighbours this pass had
    /// already moved, which makes the result depend on vertex ordering - the same mesh saved with
    /// its vertices in a different order would smooth differently.
    /// </summary>
    private static void Pass(
        Vec3[] positions,
        Vec3[] buffer,
        int[] neighbourStart,
        int[] neighbours,
        bool[] pinned,
        double factor)
    {
        for (var v = 0; v < positions.Length; v++)
        {
            var start = neighbourStart[v];
            var end = neighbourStart[v + 1];
            var degree = end - start;

            if (pinned[v] || degree == 0)
            {
                buffer[v] = positions[v];
                continue;
            }

            var sum = Vec3.Zero;
            for (var n = start; n < end; n++)
            {
                sum += positions[neighbours[n]];
            }

            var centroid = sum / degree;
            buffer[v] = positions[v] + ((centroid - positions[v]) * factor);
        }

        Array.Copy(buffer, positions, positions.Length);
    }

    /// <summary>
    /// Vertex-to-vertex adjacency in compressed row form: neighbours of v are the entries of the
    /// second array between start[v] and start[v + 1]. A neighbour is listed once per incident
    /// edge, so a vertex on a seam where two triangles share an edge sees that neighbour twice
    /// and it is weighted accordingly - which is the correct umbrella weight for a triangle
    /// mesh, since each edge contributes once per side.
    /// </summary>
    private static (int[] Start, int[] Neighbours) BuildAdjacency(int vertexCount, ImmutableArray<int> triangles)
    {
        var degrees = new int[vertexCount];
        for (var t = 0; t < triangles.Length; t += 3)
        {
            // Each corner contributes its two triangle-mates.
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
    /// Vertices on an open edge - one used by a single triangle. They are held still: averaging a
    /// rim vertex against its neighbours pulls the rim inwards, so a torn scan would have its
    /// hole widened by the act of smoothing it.
    /// </summary>
    private static bool[] FindBoundaryVertices(int vertexCount, ImmutableArray<int> triangles)
    {
        var uses = new Dictionary<(int, int), int>();
        for (var t = 0; t < triangles.Length; t += 3)
        {
            Count(uses, triangles[t], triangles[t + 1]);
            Count(uses, triangles[t + 1], triangles[t + 2]);
            Count(uses, triangles[t + 2], triangles[t]);
        }

        var pinned = new bool[vertexCount];
        foreach (var ((a, b), count) in uses)
        {
            if (count == 1)
            {
                pinned[a] = true;
                pinned[b] = true;
            }
        }

        return pinned;
    }

    private static void Count(Dictionary<(int, int), int> uses, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        uses[key] = uses.TryGetValue(key, out var count) ? count + 1 : 1;
    }
}
