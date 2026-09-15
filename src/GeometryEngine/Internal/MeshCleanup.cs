using GeometryEngine.Internal.Csg;

namespace GeometryEngine.Internal;

/// <summary>
/// Vertex and face housekeeping shared by the slices that rebuild meshes: welding coincident
/// vertices, dropping faces that describe nothing, and dropping vertices nothing refers to.
/// </summary>
internal static class MeshCleanup
{
    /// <summary>
    /// A fraction of the bounding diagonal: a little above the rounding a 32-bit float file
    /// format introduces at the model's scale, far below any feature a user put there. The same
    /// reasoning as <see cref="AdaptiveTolerance"/>, which it matches.
    /// </summary>
    public static double RelativeTolerance(IMesh mesh)
    {
        if (mesh.VertexCount == 0)
        {
            return Tolerance.Welding.Value;
        }

        var min = mesh.Vertices[0];
        var max = mesh.Vertices[0];
        foreach (var vertex in mesh.Vertices)
        {
            min = min.ComponentMin(vertex);
            max = max.ComponentMax(vertex);
        }

        return Math.Max((max - min).Length * AdaptiveTolerance.DefaultFactor, 1e-12);
    }

    /// <summary>
    /// Merges vertices within <paramref name="tolerance"/> of one another and drops the
    /// triangles whose corners merged together.
    /// </summary>
    public static (List<Vec3> Vertices, List<int> Triangles) Weld(
        IReadOnlyList<Vec3> vertices, IReadOnlyList<int> triangles, double tolerance)
    {
        var welder = new VertexWelder(tolerance);
        var remap = new int[vertices.Count];
        for (var i = 0; i < vertices.Count; i++)
        {
            remap[i] = welder.AddOrGet(vertices[i]);
        }

        var kept = new List<int>(triangles.Count);
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            var a = remap[triangles[i]];
            var b = remap[triangles[i + 1]];
            var c = remap[triangles[i + 2]];

            // A triangle whose corners welded together has no area left to contribute, and the
            // native kernel rejects a whole mesh over a single one.
            if (a == b || b == c || c == a)
            {
                continue;
            }

            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        return ([.. welder.Vertices], kept);
    }

    /// <summary>Drops vertices no triangle refers to and renumbers the rest in first-use order.</summary>
    public static (ImmutableArray<Vec3> Vertices, ImmutableArray<int> Triangles) Compact(
        IReadOnlyList<Vec3> vertices, IReadOnlyList<int> triangles)
    {
        var remap = new int[vertices.Count];
        Array.Fill(remap, -1);

        var kept = ImmutableArray.CreateBuilder<Vec3>();
        var renumbered = ImmutableArray.CreateBuilder<int>(triangles.Count);

        foreach (var index in triangles)
        {
            if (remap[index] < 0)
            {
                remap[index] = kept.Count;
                kept.Add(vertices[index]);
            }

            renumbered.Add(remap[index]);
        }

        return (kept.ToImmutable(), renumbered.MoveToImmutable());
    }

    /// <summary>
    /// Removes faces that repeat another. A repeat with the same winding is simply redundant and
    /// one copy stays; a repeat with the opposite winding is a zero-thickness sheet folded back on
    /// itself, which encloses nothing, so both go - on a closed surface that is what turns an
    /// edge carrying four faces back into one carrying two.
    /// </summary>
    public static List<int> DropRepeatedFaces(IReadOnlyList<int> triangles)
    {
        var firstBySignature = new Dictionary<(int, int, int), int>();
        var dropped = new bool[triangles.Count / 3];

        for (var t = 0; t < triangles.Count / 3; t++)
        {
            var a = triangles[t * 3];
            var b = triangles[(t * 3) + 1];
            var c = triangles[(t * 3) + 2];
            var signature = Sorted(a, b, c);

            if (!firstBySignature.TryGetValue(signature, out var first))
            {
                firstBySignature[signature] = t;
                continue;
            }

            if (dropped[first])
            {
                // Its twin already cancelled against another copy; this one starts afresh.
                firstBySignature[signature] = t;
                continue;
            }

            dropped[t] = true;
            if (!SameWinding(triangles, first, a, b, c))
            {
                dropped[first] = true;
            }
        }

        var kept = new List<int>(triangles.Count);
        for (var t = 0; t < dropped.Length; t++)
        {
            if (!dropped[t])
            {
                kept.Add(triangles[t * 3]);
                kept.Add(triangles[(t * 3) + 1]);
                kept.Add(triangles[(t * 3) + 2]);
            }
        }

        return kept;
    }

    private static bool SameWinding(IReadOnlyList<int> triangles, int triangle, int a, int b, int c)
    {
        var x = triangles[triangle * 3];
        var y = triangles[(triangle * 3) + 1];
        var z = triangles[(triangle * 3) + 2];

        // The same cyclic order, starting from any corner.
        return (x == a && y == b && z == c) || (x == b && y == c && z == a) || (x == c && y == a && z == b);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (b > c)
        {
            (b, c) = (c, b);
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        return (a, b, c);
    }
}
