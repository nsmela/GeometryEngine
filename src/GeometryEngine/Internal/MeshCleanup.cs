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

    /// <summary>
    /// <see cref="Weld"/>, but only for vertices on a seam - an edge only one triangle uses. Where
    /// the triangles already close up by index there is nothing to weld, and welding anyway can
    /// only fuse sheets the mesh holds apart.
    ///
    /// Manifold's level-set mesher - every offset - is where that bites. Where the surface touches
    /// itself it emits the point once per sheet, at exactly the same position, and a weld by
    /// position turns each such pinch into edges carrying three or four faces. A triangle soup is
    /// all seams, so it still welds whole.
    /// </summary>
    public static (List<Vec3> Vertices, List<int> Triangles) WeldSeams(
        IReadOnlyList<Vec3> vertices, IReadOnlyList<int> triangles, double tolerance)
    {
        var edgeUse = new Dictionary<(int, int), int>();
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var a = triangles[i + k];
                var b = triangles[i + ((k + 1) % 3)];
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        }

        var onSeam = new bool[vertices.Count];
        foreach (var ((a, b), uses) in edgeUse)
        {
            if (uses == 1)
            {
                onSeam[a] = true;
                onSeam[b] = true;
            }
        }

        var welder = new VertexWelder(tolerance);
        var fromWelder = new Dictionary<int, int>();
        var kept = new List<Vec3>(vertices.Count);
        var remap = new int[vertices.Count];
        for (var i = 0; i < vertices.Count; i++)
        {
            if (!onSeam[i])
            {
                remap[i] = kept.Count;
                kept.Add(vertices[i]);
                continue;
            }

            var welded = welder.AddOrGet(vertices[i]);
            if (!fromWelder.TryGetValue(welded, out var index))
            {
                index = kept.Count;
                kept.Add(vertices[i]);
                fromWelder[welded] = index;
            }

            remap[i] = index;
        }

        var result = new List<int>(triangles.Count);
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            var a = remap[triangles[i]];
            var b = remap[triangles[i + 1]];
            var c = remap[triangles[i + 2]];

            // As in Weld: a triangle whose corners welded together has no area left.
            if (a == b || b == c || c == a)
            {
                continue;
            }

            result.Add(a);
            result.Add(b);
            result.Add(c);
        }

        return (kept, result);
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
