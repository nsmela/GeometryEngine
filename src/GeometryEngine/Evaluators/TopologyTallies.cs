namespace GeometryEngine.Evaluators;

/// <summary>What the edges of a mesh add up to.</summary>
/// <param name="Edges">Distinct undirected edges.</param>
/// <param name="Boundary">Edges one triangle uses.</param>
/// <param name="NonManifold">Edges more than two triangles use.</param>
/// <param name="InconsistentWinding">
/// Times an edge is walked in a direction it has already been walked in: two faces sharing it
/// with the same orientation, so one of them is wound backwards.
/// </param>
internal readonly record struct EdgeTally(int Edges, int Boundary, int NonManifold, int InconsistentWinding);

/// <summary>
/// The counts behind the topology audit, each made by sorting indices into buckets rather than
/// by hashing them.
///
/// The audit used to keep a dictionary of edges, a set of directed edges and a set of faces: three
/// hash tables of tuples, grown one insertion at a time, and most of the 38 MB it allocated on a
/// 100k-triangle mesh. An edge belongs to its lower vertex and a face to its lowest,
/// so one counting pass says how many each vertex owns and a second drops them into place, in
/// flat arrays allocated once at their final size. A vertex owns three edges on average, so
/// putting each bucket in order is nearly free, and equal neighbours in it are the same edge.
/// </summary>
internal static class TopologyTallies
{
    /// <summary>
    /// Tallies the edges of every triangle whose three corners differ. A triangle with a repeated
    /// corner has no edges to pair and is left out, as the audit has always left it out.
    /// </summary>
    public static EdgeTally Edges(ReadOnlySpan<int> triangles, int vertexCount)
    {
        // start[v] .. start[v + 1] is the bucket of edges whose lower end is v.
        var start = new int[vertexCount + 1];
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);
            if (a == b || b == c || c == a)
            {
                continue;
            }

            start[Math.Min(a, b) + 1]++;
            start[Math.Min(b, c) + 1]++;
            start[Math.Min(c, a) + 1]++;
        }

        for (var v = 0; v < vertexCount; v++)
        {
            start[v + 1] += start[v];
        }

        // Each entry is the edge's higher end with the direction it was walked in below it: one
        // bit, which an index leaves free in thirty-two. Sorting a bucket then brings an edge's
        // uses together, those walked upward first.
        var entries = new uint[start[vertexCount]];
        var next = start.AsSpan(0, vertexCount).ToArray();
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);
            if (a == b || b == c || c == a)
            {
                continue;
            }

            Place(entries, next, a, b);
            Place(entries, next, b, c);
            Place(entries, next, c, a);
        }

        var (edges, boundary, nonManifold, inconsistent) = (0, 0, 0, 0);
        for (var v = 0; v < vertexCount; v++)
        {
            var bucket = entries.AsSpan(start[v], start[v + 1] - start[v]);
            if (bucket.Length > 1)
            {
                bucket.Sort();
            }

            for (var i = 0; i < bucket.Length;)
            {
                var higher = bucket[i] >> 1;
                var (upward, uses) = (0, 0);
                for (; i < bucket.Length && bucket[i] >> 1 == higher; i++)
                {
                    uses++;
                    upward += (bucket[i] & 1) == 0 ? 1 : 0;
                }

                edges++;
                boundary += uses == 1 ? 1 : 0;
                nonManifold += uses > 2 ? 1 : 0;
                inconsistent += Math.Max(0, upward - 1) + Math.Max(0, uses - upward - 1);
            }
        }

        return new EdgeTally(edges, boundary, nonManifold, inconsistent);
    }

    /// <summary>
    /// How many triangles repeat the three corners of one before them, in any order. Every
    /// triangle is counted, one with a repeated corner included.
    /// </summary>
    public static int DuplicateFaces(ReadOnlySpan<int> triangles, int vertexCount)
    {
        var start = new int[vertexCount + 1];
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            start[Math.Min(triangles[t], Math.Min(triangles[t + 1], triangles[t + 2])) + 1]++;
        }

        for (var v = 0; v < vertexCount; v++)
        {
            start[v + 1] += start[v];
        }

        var entries = new ulong[start[vertexCount]];
        var next = start.AsSpan(0, vertexCount).ToArray();
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);
            if (a > b) { (a, b) = (b, a); }
            if (b > c) { (b, c) = (c, b); }
            if (a > b) { (a, b) = (b, a); }

            entries[next[a]++] = ((ulong)(uint)b << 32) | (uint)c;
        }

        var duplicates = 0;
        for (var v = 0; v < vertexCount; v++)
        {
            var bucket = entries.AsSpan(start[v], start[v + 1] - start[v]);
            if (bucket.Length < 2)
            {
                continue;
            }

            bucket.Sort();
            for (var i = 1; i < bucket.Length; i++)
            {
                duplicates += bucket[i] == bucket[i - 1] ? 1 : 0;
            }
        }

        return duplicates;
    }

    private static void Place(uint[] entries, int[] next, int from, int to)
    {
        var (lower, higher, downward) = from < to ? (from, to, 0u) : (to, from, 1u);
        entries[next[lower]++] = ((uint)higher << 1) | downward;
    }
}
