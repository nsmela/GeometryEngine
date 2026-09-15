namespace GeometryEngine.Internal.Csg;

/// <summary>
/// Turns the loose polygon soup the CSG kernel produces into an indexed,
/// watertight triangle mesh.
///
/// Two things have to be repaired. First, the same cut point is computed
/// independently by neighbouring polygons, so coincident vertices must be welded.
/// Second, a BSP splits a polygon only along the planes it happens to meet on its
/// way down the tree, so a neighbour may be left unsplit and a T-vertex appears in
/// the middle of its edge. A T-vertex is not a hole in the mathematical sense but
/// it breaks edge pairing, and every downstream consumer, from volume integration
/// to slicing for print, treats it as one. Both repairs happen here so that the
/// kernel itself stays purely combinatorial.
/// </summary>
internal static class MeshHealer
{
    public static Result<IMesh> Build(
        IReadOnlyList<CsgPolygon> polygons,
        MeshMetadata metadata,
        Tolerance tolerance)
    {
        var welder = new VertexWelder(tolerance.Value);
        var triangles = new List<int>();

        foreach (var polygon in polygons)
        {
            Triangulate(polygon, welder, triangles, tolerance);
        }

        if (triangles.Count == 0)
        {
            return Result.Success<IMesh>(ImmutableMesh.Empty.WithMetadata(metadata));
        }

        var vertices = new List<Vec3>(welder.Vertices);

        // Weld first, then close the T-junctions so the mesh is conforming - every
        // boundary vertex shared by exactly the faces that meet there. Only then
        // collapse the redundant coplanar triangulation: on conforming input the merge
        // keeps every boundary vertex a neighbour relies on, so it introduces no new
        // T-junctions and needs no second repair pass. It also undoes the centroid fans
        // the repair itself adds, since those interior edges cancel.
        var repaired = RepairTJunctions(vertices, triangles, tolerance);
        var merged = CoplanarMerge.Merge(vertices, repaired, tolerance);

        return Compact(vertices, merged, metadata);
    }

    /// <summary>
    /// Fans a convex polygon into triangles. Kernel polygons are always convex, so a
    /// fan from the first corner is safe and needs no ear clipping.
    /// </summary>
    private static void Triangulate(
        CsgPolygon polygon,
        VertexWelder welder,
        List<int> triangles,
        Tolerance tolerance)
    {
        var loop = new int[polygon.Vertices.Length];
        for (var i = 0; i < loop.Length; i++)
        {
            loop[i] = welder.AddOrGet(polygon.Vertices[i]);
        }

        for (var i = 1; i + 1 < loop.Length; i++)
        {
            Emit(triangles, welder.Vertices, loop[0], loop[i], loop[i + 1], tolerance);
        }
    }

    private static void Emit(List<int> triangles, IReadOnlyList<Vec3> vertices, int a, int b, int c, Tolerance tolerance)
    {
        if (a == b || b == c || c == a)
        {
            return;
        }

        var area = (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]).Length * 0.5;
        if (area <= tolerance.Value * tolerance.Value)
        {
            return;
        }

        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    /// <summary>
    /// Re-triangulates any triangle that has other vertices sitting on its edges.
    /// The augmented outline is still convex, but a fan from a corner would produce
    /// slivers along that corner's own edges, so the fan starts from the centroid.
    /// </summary>
    private static List<int> RepairTJunctions(List<Vec3> vertices, List<int> triangles, Tolerance tolerance)
    {
        var grid = new PointGrid(vertices, CellSizeFor(vertices, triangles));
        var repaired = new List<int>(triangles.Count);
        var candidates = new HashSet<int>();
        var outline = new List<int>(8);

        for (var t = 0; t < triangles.Count; t += 3)
        {
            var corners = new[] { triangles[t], triangles[t + 1], triangles[t + 2] };
            outline.Clear();
            var found = false;

            for (var edge = 0; edge < 3; edge++)
            {
                var from = corners[edge];
                var to = corners[(edge + 1) % 3];
                outline.Add(from);

                var extras = PointsOnEdge(vertices, grid, candidates, corners, from, to, tolerance);
                if (extras.Count > 0)
                {
                    found = true;
                    outline.AddRange(extras);
                }
            }

            if (!found)
            {
                repaired.Add(corners[0]);
                repaired.Add(corners[1]);
                repaired.Add(corners[2]);
                continue;
            }

            var centroid = (vertices[corners[0]] + vertices[corners[1]] + vertices[corners[2]]) / 3.0;
            var hub = vertices.Count;
            vertices.Add(centroid);

            for (var i = 0; i < outline.Count; i++)
            {
                Emit(repaired, vertices, hub, outline[i], outline[(i + 1) % outline.Count], tolerance);
            }
        }

        return repaired;
    }

    private static List<int> PointsOnEdge(
        List<Vec3> vertices,
        PointGrid grid,
        HashSet<int> candidates,
        int[] corners,
        int from,
        int to,
        Tolerance tolerance)
    {
        var start = vertices[from];
        var direction = vertices[to] - start;
        var lengthSquared = direction.LengthSquared;
        var found = new List<(double Parameter, int Index)>();

        if (lengthSquared <= 0)
        {
            return [];
        }

        candidates.Clear();
        grid.CollectNear(start, vertices[to], candidates);

        foreach (var candidate in candidates)
        {
            if (candidate == corners[0] || candidate == corners[1] || candidate == corners[2])
            {
                continue;
            }

            var offset = vertices[candidate] - start;
            var parameter = offset.Dot(direction) / lengthSquared;
            if (parameter <= 0 || parameter >= 1)
            {
                continue;
            }

            var deviation = offset - (direction * parameter);
            if (deviation.LengthSquared > tolerance.Value * tolerance.Value)
            {
                continue;
            }

            found.Add((parameter, candidate));
        }

        if (found.Count == 0)
        {
            return [];
        }

        // Sorting on the index as well keeps the output independent of hash order.
        found.Sort((left, right) => left.Parameter != right.Parameter
            ? left.Parameter.CompareTo(right.Parameter)
            : left.Index.CompareTo(right.Index));

        return [.. found.Select(entry => entry.Index)];
    }

    /// <summary>A grid roughly the size of a typical edge keeps neighbour queries cheap.</summary>
    private static double CellSizeFor(IReadOnlyList<Vec3> vertices, List<int> triangles)
    {
        var total = 0.0;
        var counted = 0;

        for (var t = 0; t < triangles.Count; t += 3)
        {
            total += vertices[triangles[t]].DistanceTo(vertices[triangles[t + 1]]);
            counted++;
        }

        var mean = counted == 0 ? 1.0 : total / counted;
        return mean > 1e-9 ? mean : 1e-9;
    }

    /// <summary>Drops vertices no triangle refers to and renumbers what is left.</summary>
    private static Result<IMesh> Compact(List<Vec3> vertices, List<int> triangles, MeshMetadata metadata)
    {
        var remapped = new int[vertices.Count];
        Array.Fill(remapped, -1);

        var kept = ImmutableArray.CreateBuilder<Vec3>();
        var indices = ImmutableArray.CreateBuilder<int>(triangles.Count);

        foreach (var index in triangles)
        {
            if (remapped[index] < 0)
            {
                remapped[index] = kept.Count;
                kept.Add(vertices[index]);
            }

            indices.Add(remapped[index]);
        }

        return ImmutableMesh.Create(kept.ToImmutable(), indices.MoveToImmutable(), metadata);
    }
}

/// <summary>Reads an <see cref="IMesh"/> as the polygon soup the kernel works on.</summary>
internal static class MeshPolygons
{
    public static List<CsgPolygon> From(IMesh mesh)
    {
        var polygons = new List<CsgPolygon>(mesh.TriangleCount);

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.TriangleAt(t);
            var polygon = CsgPolygon.FromVertices(ImmutableArray.Create(a, b, c));

            if (polygon.HasValue)
            {
                polygons.Add(polygon.Value);
            }
        }

        return polygons;
    }
}
