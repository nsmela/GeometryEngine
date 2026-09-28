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
    ///
    /// A mesh that is already edge-manifold is returned untouched, because there is nothing left
    /// for a weld to recover and something for it to destroy. A level-set mesher legitimately
    /// emits a *pinch*: two distinct vertices at one position, where the surface touches itself
    /// at a point. Every edge still carries two faces, so the mesh is manifold, and the sheets
    /// are held apart only by those being separate indices. Welding on position alone fuses them,
    /// and an edge both sheets reach then carries four faces - which is how a manifold offset
    /// surface came out of <see cref="Decimation.MeshDecimator"/> non-manifold, and reached a
    /// slicer that way. Input that actually needs welding - a triangle soup out of an STL, where
    /// every edge starts with one face - is nowhere near manifold and takes the same path it
    /// always did.
    /// </summary>
    public static (List<Vec3> Vertices, List<int> Triangles) Weld(
        IReadOnlyList<Vec3> vertices, IReadOnlyList<int> triangles, double tolerance)
    {
        if (IsEdgeManifold(triangles))
        {
            return ([.. vertices], [.. triangles]);
        }

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
    /// True when every edge is shared by exactly two triangles, which for these purposes is
    /// enough: such a mesh already carries its own topology in its indices, so welding could only
    /// take topology away.
    ///
    /// Deliberately not a full manifold test - winding, shells and pinched vertices are not
    /// consulted. The question here is only whether the indices already describe a surface, and
    /// a soup that needs welding fails on the first edge it reaches.
    /// </summary>
    private static bool IsEdgeManifold(IReadOnlyList<int> triangles)
    {
        if (triangles.Count < 3)
        {
            return false;
        }

        var facesPerEdge = new Dictionary<(int, int), int>(triangles.Count);
        for (var t = 0; t + 2 < triangles.Count; t += 3)
        {
            var a = triangles[t];
            var b = triangles[t + 1];
            var c = triangles[t + 2];
            if (a == b || b == c || c == a)
            {
                return false;
            }

            Count(facesPerEdge, a, b);
            Count(facesPerEdge, b, c);
            Count(facesPerEdge, c, a);

            // A closed surface has three edges per two triangles. Past that there is no way back
            // to two faces each, so a soup - which starts with three per triangle - stops here
            // rather than counting its way to the end.
            if (facesPerEdge.Count > triangles.Count / 2)
            {
                return false;
            }
        }

        foreach (var faces in facesPerEdge.Values)
        {
            if (faces != 2)
            {
                return false;
            }
        }

        return true;
    }

    private static void Count(Dictionary<(int, int), int> facesPerEdge, int from, int to)
    {
        var edge = from < to ? (from, to) : (to, from);
        facesPerEdge[edge] = facesPerEdge.TryGetValue(edge, out var faces) ? faces + 1 : 1;
    }

    /// <summary>
    /// Eases apart vertices that sit at one position, so the surface no longer touches itself
    /// there.
    ///
    /// A pinch survives inside this library, where two indices at one position are two vertices.
    /// It does not survive being written out: an STL has no indices at all, its coordinates are
    /// 32-bit, and the reader on the other side recovers topology by welding on position - so it
    /// fuses the pair and hands a slicer an edge with four faces. Nothing downstream can undo
    /// that, because by then the information is gone.
    ///
    /// Each vertex is moved a hair towards the middle of its own neighbours, which is along its
    /// own sheet and therefore away from the other one. The step is four weld tolerances - about
    /// a ten-thousandth of a millimetre on a bolus, several times what a 32-bit float can still
    /// tell apart at that scale, and orders below both the grid the surface was meshed on and
    /// anything a printer resolves.
    /// </summary>
    public static void SeparateCoincidentVertices(
        List<Vec3> vertices, IReadOnlyList<int> triangles, double tolerance)
    {
        var welder = new VertexWelder(tolerance);
        var group = new int[vertices.Count];
        var members = new Dictionary<int, List<int>>();
        for (var i = 0; i < vertices.Count; i++)
        {
            group[i] = welder.AddOrGet(vertices[i]);
            if (!members.TryGetValue(group[i], out var list))
            {
                members[group[i]] = list = [];
            }

            list.Add(i);
        }

        var pinched = new HashSet<int>();
        foreach (var list in members.Values)
        {
            if (list.Count > 1)
            {
                pinched.UnionWith(list);
            }
        }

        if (pinched.Count == 0)
        {
            return;
        }

        var neighbourSum = new Dictionary<int, Vec3>(pinched.Count);
        var neighbourCount = new Dictionary<int, int>(pinched.Count);
        foreach (var vertex in pinched)
        {
            neighbourSum[vertex] = Vec3.Zero;
            neighbourCount[vertex] = 0;
        }

        for (var t = 0; t + 2 < triangles.Count; t += 3)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = triangles[t + corner];
                if (!pinched.Contains(vertex))
                {
                    continue;
                }

                neighbourSum[vertex] += vertices[triangles[t + ((corner + 1) % 3)]]
                                      + vertices[triangles[t + ((corner + 2) % 3)]];
                neighbourCount[vertex] += 2;
            }
        }

        var step = 4 * tolerance;
        foreach (var vertex in pinched)
        {
            var count = neighbourCount[vertex];
            if (count == 0)
            {
                continue;
            }

            var towards = (neighbourSum[vertex] / count) - vertices[vertex];
            var reach = towards.Length;

            // A vertex whose neighbours average out onto it has no direction to move along, and
            // one closer to them than the step would be dragged past them.
            if (reach <= step)
            {
                continue;
            }

            vertices[vertex] += towards * (step / reach);
        }
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
