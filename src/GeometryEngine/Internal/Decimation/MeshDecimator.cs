namespace GeometryEngine.Internal.Decimation;

/// <summary>
/// Reduces a mesh towards a target triangle count by quadric edge collapse (Garland-Heckbert):
/// each vertex carries the summed squared distance to the planes of the faces around it, so
/// collapsing the edge with the smallest added error keeps the silhouette while flat interiors
/// give way first.
///
/// The native kernel has no equivalent - its design is exactness, not approximation.
/// </summary>
internal static class MeshDecimator
{
    /// <summary>Below this a quadric is too near-singular to solve for an optimal position.</summary>
    private const double SingularQuadric = 1e-12;

    public static (ImmutableArray<Vec3> Vertices, ImmutableArray<int> Triangles) Decimate(
        IMesh mesh, int targetTriangleCount, double weldTolerance)
    {
        var (welded, triangles) = MeshCleanup.Weld(mesh.Vertices, mesh.Triangles, weldTolerance);
        var triangleCount = triangles.Count / 3;

        if (triangleCount <= targetTriangleCount || targetTriangleCount < 4)
        {
            // Below the target there is nothing to collapse, but the promise that no two vertices
            // leave here at one position holds whether or not any work was done.
            MeshCleanup.SeparateCoincidentVertices(welded, triangles, weldTolerance);
            return MeshCleanup.Compact(welded, triangles);
        }

        var positions = welded.ToArray();
        var quadrics = new Quadric[positions.Length];
        var alive = new bool[triangleCount];
        Array.Fill(alive, true);

        // Vertices merge by union-find rather than being rewritten in place, so a collapse stays
        // O(1) instead of touching every triangle that used the vertex.
        var merged = new int[positions.Length];
        for (var i = 0; i < merged.Length; i++)
        {
            merged[i] = i;
        }

        int Find(int x)
        {
            while (merged[x] != x)
            {
                merged[x] = merged[merged[x]];
                x = merged[x];
            }

            return x;
        }

        for (var t = 0; t < triangleCount; t++)
        {
            var plane = PlaneOf(positions[triangles[t * 3]], positions[triangles[(t * 3) + 1]], positions[triangles[(t * 3) + 2]]);
            if (plane is null)
            {
                continue;
            }

            var quadric = Quadric.FromPlane(plane.Value);
            quadrics[triangles[t * 3]] += quadric;
            quadrics[triangles[(t * 3) + 1]] += quadric;
            quadrics[triangles[(t * 3) + 2]] += quadric;
        }

        var edgeUse = new Dictionary<(int, int), int>();
        for (var t = 0; t < triangleCount; t++)
        {
            AddEdgeUse(edgeUse, triangles[t * 3], triangles[(t * 3) + 1]);
            AddEdgeUse(edgeUse, triangles[(t * 3) + 1], triangles[(t * 3) + 2]);
            AddEdgeUse(edgeUse, triangles[(t * 3) + 2], triangles[t * 3]);
        }

        // Every collapse moves a vertex and changes its quadric, which re-prices every edge around
        // it. The queue cannot re-prioritise in place, so each entry carries the versions of its
        // endpoints at the time it was priced, and an entry whose endpoints have moved since is
        // discarded when it surfaces - its replacement, at the true cost, was queued by the
        // collapse that moved them. Filtering on union-find roots alone is not enough: an edge
        // priced before its endpoint was merged would otherwise collapse at the old, cheaper cost,
        // which is how a decimated bolus loses a sixth of its volume.
        var versions = new int[positions.Length];
        var queue = new PriorityQueue<(int A, int B, int VersionA, int VersionB), double>();
        foreach (var (edge, uses) in edgeUse)
        {
            // A boundary edge is never collapsed, so an open mesh keeps its rim.
            if (uses != 1)
            {
                queue.Enqueue((edge.Item1, edge.Item2, 0, 0), CollapseCost(quadrics, positions, edge.Item1, edge.Item2));
            }
        }

        var trianglesPerVertex = BuildVertexTriangles(triangles, positions.Length);
        Func<int, int> find = Find;

        var live = triangleCount;
        while (live > targetTriangleCount && queue.Count > 0)
        {
            var (a, b, versionA, versionB) = queue.Dequeue();

            // An endpoint merged away, or moved, since this entry was priced.
            if (merged[a] != a || merged[b] != b || versions[a] != versionA || versions[b] != versionB)
            {
                continue;
            }

            var rootA = a;
            var rootB = b;

            if (!SatisfiesLinkCondition(triangles, alive, trianglesPerVertex, find, rootA, rootB))
            {
                continue;
            }

            var target = OptimalPosition(quadrics[rootA] + quadrics[rootB], positions[rootA], positions[rootB]);

            if (FoldsOver(triangles, alive, trianglesPerVertex, positions, find, rootA, rootB, target) ||
                FoldsOver(triangles, alive, trianglesPerVertex, positions, find, rootB, rootA, target))
            {
                continue;
            }

            // Collapse b into a, then retire every triangle that has lost a distinct corner.
            merged[rootB] = rootA;
            positions[rootA] = target;
            quadrics[rootA] += quadrics[rootB];
            versions[rootA]++;

            foreach (var t in trianglesPerVertex[rootB])
            {
                if (!alive[t])
                {
                    continue;
                }

                var v0 = Find(triangles[t * 3]);
                var v1 = Find(triangles[(t * 3) + 1]);
                var v2 = Find(triangles[(t * 3) + 2]);
                if (v0 == v1 || v1 == v2 || v2 == v0)
                {
                    alive[t] = false;
                    live--;
                }
            }

            // The merged vertex inherits b's triangles so later collapses see them, less the
            // ones that just died.
            trianglesPerVertex[rootA].AddRange(trianglesPerVertex[rootB]);
            trianglesPerVertex[rootB].Clear();
            trianglesPerVertex[rootA].RemoveAll(t => !alive[t]);

            // Re-price the edges around the new vertex; stale entries are filtered on dequeue by
            // the root check above.
            var neighbours = new HashSet<int>();
            foreach (var t in trianglesPerVertex[rootA])
            {
                for (var i = 0; i < 3; i++)
                {
                    var neighbour = Find(triangles[(t * 3) + i]);
                    if (neighbour != rootA)
                    {
                        neighbours.Add(neighbour);
                    }
                }
            }

            foreach (var neighbour in neighbours)
            {
                queue.Enqueue(
                    (rootA, neighbour, versions[rootA], versions[neighbour]),
                    CollapseCost(quadrics, positions, rootA, neighbour));
            }
        }

        var result = new List<int>(live * 3);
        for (var t = 0; t < triangleCount; t++)
        {
            if (!alive[t])
            {
                continue;
            }

            var v0 = Find(triangles[t * 3]);
            var v1 = Find(triangles[(t * 3) + 1]);
            var v2 = Find(triangles[(t * 3) + 2]);
            if (v0 != v1 && v1 != v2 && v2 != v0)
            {
                result.Add(v0);
                result.Add(v1);
                result.Add(v2);
            }
        }

        // Collapses land on positions a quadric chose, and two of them can land on the same one -
        // as can a pinch the offset surface arrived with. Either way the mesh leaving here is the
        // one that gets written out, and a 32-bit file cannot hold two vertices at one position:
        // the reader welds them, and a manifold surface reaches the slicer with a four-faced edge.
        var separated = new List<Vec3>(positions);
        MeshCleanup.SeparateCoincidentVertices(separated, result, weldTolerance);

        return MeshCleanup.Compact(separated, result);
    }

    /// <summary>
    /// True when collapsing the edge cannot break the surface's topology: the endpoints may share
    /// only the vertices opposite the edge itself. Any other shared neighbour means the collapse
    /// folds two parts of the surface onto each other and leaves an edge carrying three faces.
    ///
    /// Skipping this is not cosmetic. A non-manifold decimation poisons everything downstream:
    /// the native kernel refuses it, and an offset built on its distance field returns noise.
    /// </summary>
    private static bool SatisfiesLinkCondition(
        List<int> triangles, bool[] alive, List<int>[] trianglesPerVertex, Func<int, int> find, int rootA, int rootB)
    {
        var neighboursA = Neighbours(triangles, alive, trianglesPerVertex, find, rootA);
        var neighboursB = Neighbours(triangles, alive, trianglesPerVertex, find, rootB);
        var shared = neighboursA.Count(neighboursB.Contains);

        var sharedFaces = 0;
        foreach (var t in trianglesPerVertex[rootA])
        {
            if (!alive[t])
            {
                continue;
            }

            var hasA = false;
            var hasB = false;
            for (var i = 0; i < 3; i++)
            {
                var v = find(triangles[(t * 3) + i]);
                hasA |= v == rootA;
                hasB |= v == rootB;
            }

            if (hasA && hasB)
            {
                sharedFaces++;
            }
        }

        return sharedFaces > 0 && shared == sharedFaces;
    }

    /// <summary>
    /// True when moving <paramref name="root"/> to <paramref name="target"/> would turn any of its
    /// surviving triangles inside out or flatten one. The quadric knows nothing of orientation:
    /// the point minimising squared distance to the surrounding planes can sit beyond a
    /// neighbouring triangle, and a flipped triangle is a surface passing through itself.
    /// </summary>
    private static bool FoldsOver(
        List<int> triangles,
        bool[] alive,
        List<int>[] trianglesPerVertex,
        Vec3[] positions,
        Func<int, int> find,
        int root,
        int collapsingInto,
        Vec3 target)
    {
        foreach (var t in trianglesPerVertex[root])
        {
            if (!alive[t])
            {
                continue;
            }

            var v0 = find(triangles[t * 3]);
            var v1 = find(triangles[(t * 3) + 1]);
            var v2 = find(triangles[(t * 3) + 2]);

            // Triangles on the collapsing edge disappear, so their orientation is moot.
            if (v0 == collapsingInto || v1 == collapsingInto || v2 == collapsingInto)
            {
                continue;
            }

            var before = Normal(positions[v0], positions[v1], positions[v2]);
            if (before is null)
            {
                continue;
            }

            var after = Normal(
                v0 == root ? target : positions[v0],
                v1 == root ? target : positions[v1],
                v2 == root ? target : positions[v2]);

            if (after is null || before.Value.Dot(after.Value) <= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static Vec3? Normal(Vec3 a, Vec3 b, Vec3 c)
    {
        var normal = (b - a).Cross(c - a);
        var length = normal.Length;
        return length < 1e-14 ? null : normal / length;
    }

    private static HashSet<int> Neighbours(
        List<int> triangles, bool[] alive, List<int>[] trianglesPerVertex, Func<int, int> find, int root)
    {
        var neighbours = new HashSet<int>();
        foreach (var t in trianglesPerVertex[root])
        {
            if (!alive[t])
            {
                continue;
            }

            for (var i = 0; i < 3; i++)
            {
                var v = find(triangles[(t * 3) + i]);
                if (v != root)
                {
                    neighbours.Add(v);
                }
            }
        }

        return neighbours;
    }

    private static List<int>[] BuildVertexTriangles(List<int> triangles, int vertexCount)
    {
        var result = new List<int>[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            result[i] = [];
        }

        for (var t = 0; t < triangles.Count / 3; t++)
        {
            result[triangles[t * 3]].Add(t);
            result[triangles[(t * 3) + 1]].Add(t);
            result[triangles[(t * 3) + 2]].Add(t);
        }

        return result;
    }

    private static void AddEdgeUse(Dictionary<(int, int), int> edges, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        edges[key] = edges.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static double CollapseCost(Quadric[] quadrics, Vec3[] positions, int a, int b)
    {
        var quadric = quadrics[a] + quadrics[b];
        return Math.Max(0, quadric.Evaluate(OptimalPosition(quadric, positions[a], positions[b])));
    }

    /// <summary>
    /// The position minimising the combined quadric, or the best of the endpoints and midpoint
    /// when it is too flat to invert - a planar region, where any point on the edge costs the same.
    /// </summary>
    private static Vec3 OptimalPosition(Quadric quadric, Vec3 a, Vec3 b)
    {
        if (quadric.TrySolve(out var solved))
        {
            return solved;
        }

        var midpoint = (a + b) * 0.5;
        var costA = quadric.Evaluate(a);
        var costB = quadric.Evaluate(b);
        var costMid = quadric.Evaluate(midpoint);

        if (costMid <= costA && costMid <= costB)
        {
            return midpoint;
        }

        return costA <= costB ? a : b;
    }

    private static (double A, double B, double C, double D)? PlaneOf(Vec3 a, Vec3 b, Vec3 c)
    {
        var normal = (b - a).Cross(c - a);
        var length = normal.Length;
        if (length < 1e-14)
        {
            return null;
        }

        normal /= length;
        return (normal.X, normal.Y, normal.Z, -normal.Dot(a));
    }

    /// <summary>Symmetric 4x4 error quadric, stored as its ten distinct entries.</summary>
    private struct Quadric
    {
        public double Q00, Q01, Q02, Q03, Q11, Q12, Q13, Q22, Q23, Q33;

        public static Quadric FromPlane((double A, double B, double C, double D) plane)
        {
            var (a, b, c, d) = plane;
            return new Quadric
            {
                Q00 = a * a, Q01 = a * b, Q02 = a * c, Q03 = a * d,
                Q11 = b * b, Q12 = b * c, Q13 = b * d,
                Q22 = c * c, Q23 = c * d,
                Q33 = d * d,
            };
        }

        public static Quadric operator +(Quadric x, Quadric y) => new()
        {
            Q00 = x.Q00 + y.Q00, Q01 = x.Q01 + y.Q01, Q02 = x.Q02 + y.Q02, Q03 = x.Q03 + y.Q03,
            Q11 = x.Q11 + y.Q11, Q12 = x.Q12 + y.Q12, Q13 = x.Q13 + y.Q13,
            Q22 = x.Q22 + y.Q22, Q23 = x.Q23 + y.Q23,
            Q33 = x.Q33 + y.Q33,
        };

        public readonly double Evaluate(Vec3 v) =>
            (Q00 * v.X * v.X) + (2 * Q01 * v.X * v.Y) + (2 * Q02 * v.X * v.Z) + (2 * Q03 * v.X) +
            (Q11 * v.Y * v.Y) + (2 * Q12 * v.Y * v.Z) + (2 * Q13 * v.Y) +
            (Q22 * v.Z * v.Z) + (2 * Q23 * v.Z) +
            Q33;

        /// <summary>Solves the 3x3 block for the minimising point, by Cramer's rule.</summary>
        public readonly bool TrySolve(out Vec3 result)
        {
            result = default;

            var determinant =
                (Q00 * ((Q11 * Q22) - (Q12 * Q12))) -
                (Q01 * ((Q01 * Q22) - (Q12 * Q02))) +
                (Q02 * ((Q01 * Q12) - (Q11 * Q02)));

            if (Math.Abs(determinant) < SingularQuadric)
            {
                return false;
            }

            var x = -((Q03 * ((Q11 * Q22) - (Q12 * Q12))) - (Q01 * ((Q13 * Q22) - (Q12 * Q23))) + (Q02 * ((Q13 * Q12) - (Q11 * Q23))));
            var y = -((Q00 * ((Q13 * Q22) - (Q12 * Q23))) - (Q03 * ((Q01 * Q22) - (Q02 * Q12))) + (Q02 * ((Q01 * Q23) - (Q13 * Q02))));
            var z = -((Q00 * ((Q11 * Q23) - (Q13 * Q12))) - (Q01 * ((Q01 * Q23) - (Q13 * Q02))) + (Q03 * ((Q01 * Q12) - (Q11 * Q02))));

            result = new Vec3(x / determinant, y / determinant, z / determinant);
            return result.IsFinite;
        }
    }
}
