using GeometryEngine.Internal;
using GeometryEngine.Internal.Csg;

namespace GeometryEngine.Evaluators;

/// <summary>Ask for the aggregate measurements of a mesh.</summary>
public sealed record StatisticsRequest(IMesh Mesh);

/// <summary>
/// Volume comes from the divergence theorem: the signed volume of the tetrahedron
/// spanned by the origin and each triangle, summed. It is exact for any closed
/// mesh and independent of where the origin sits.
/// </summary>
internal sealed class StatisticsHandler
{
    public Result<MeshStatistics> Handle(StatisticsRequest request)
    {
        var mesh = request.Mesh;
        if (mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var volumeTimesSix = 0.0;
        var areaTimesTwo = 0.0;
        var min = mesh.Vertices[0];
        var max = mesh.Vertices[0];

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.TriangleAt(t);
            var cross = (b - a).Cross(c - a);

            volumeTimesSix += a.Dot(b.Cross(c));
            areaTimesTwo += cross.Length;
        }

        foreach (var vertex in mesh.Vertices)
        {
            min = min.ComponentMin(vertex);
            max = max.ComponentMax(vertex);
        }

        return new MeshStatistics(
            volumeTimesSix / 6.0,
            areaTimesTwo / 2.0,
            min,
            max,
            mesh.VertexCount,
            mesh.TriangleCount);
    }
}

/// <summary>Ask whether a mesh is a closed, manifold surface.</summary>
public sealed record TopologyRequest(IMesh Mesh);

internal sealed class TopologyHandler(Tolerance tolerance)
{
    private readonly Tolerance _tolerance = tolerance;

    public Result<TopologyValidation> Handle(TopologyRequest request)
    {
        var mesh = request.Mesh;
        if (mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var edgeUses = new Dictionary<(int Low, int High), int>();
        var directedEdges = new HashSet<(int From, int To)>();
        var faceSignatures = new HashSet<(int, int, int)>();
        var shells = new DisjointSet(mesh.VertexCount);

        var degenerate = 0;
        var duplicateFaces = 0;
        var inconsistentWinding = 0;

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var offset = t * 3;
            var a = mesh.Triangles[offset];
            var b = mesh.Triangles[offset + 1];
            var c = mesh.Triangles[offset + 2];

            // Connectivity for the shell count spans every triangle, degenerate or not.
            shells.Union(a, b);
            shells.Union(b, c);

            if (!faceSignatures.Add(Sorted(a, b, c)))
            {
                duplicateFaces++;
            }

            // A triangle whose corners share an index has no edges to pair, so it is left out of
            // the edge tally. A sliver - distinct corners, no area - is not: it is still part of the
            // surface's connectivity, and a closed boolean result routinely carries them where one
            // operand's vertex lands on another's edge. Dropping its edges would report holes in a
            // surface that has none, and every volume computed downstream would read as zero.
            if (a == b || b == c || c == a)
            {
                degenerate++;
                continue;
            }

            if (IsSliver(mesh, a, b, c))
            {
                degenerate++;
            }

            // A consistently wound manifold traverses each half-edge exactly once. A
            // half-edge seen twice in the same direction means two faces share it with
            // the same orientation - one of them is wound backwards (an inverted face).
            if (!directedEdges.Add((a, b))) { inconsistentWinding++; }
            if (!directedEdges.Add((b, c))) { inconsistentWinding++; }
            if (!directedEdges.Add((c, a))) { inconsistentWinding++; }

            Record(edgeUses, a, b);
            Record(edgeUses, b, c);
            Record(edgeUses, c, a);
        }

        var boundary = edgeUses.Values.Count(uses => uses == 1);
        var nonManifold = edgeUses.Values.Count(uses => uses > 2);

        var referenced = new bool[mesh.VertexCount];
        foreach (var index in mesh.Triangles)
        {
            referenced[index] = true;
        }

        return new TopologyValidation(
            boundary,
            nonManifold,
            degenerate,
            CountDuplicateVertices(mesh),
            inconsistentWinding,
            duplicateFaces,
            shells.CountRootsAmong(mesh.Triangles))
        {
            EdgeCount = edgeUses.Count,
            UnreferencedVertexCount = referenced.Count(used => !used),
        };
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) { (a, b) = (b, a); }
        if (b > c) { (b, c) = (c, b); }
        if (a > b) { (a, b) = (b, a); }
        return (a, b, c);
    }

    private bool IsSliver(IMesh mesh, int a, int b, int c)
    {
        var area = (mesh.Vertices[b] - mesh.Vertices[a]).Cross(mesh.Vertices[c] - mesh.Vertices[a]).Length * 0.5;
        return area <= _tolerance.Value * _tolerance.Value;
    }

    private static void Record(Dictionary<(int, int), int> edgeUses, int from, int to)
    {
        var key = from < to ? (from, to) : (to, from);
        edgeUses[key] = edgeUses.TryGetValue(key, out var uses) ? uses + 1 : 1;
    }

    private int CountDuplicateVertices(IMesh mesh)
    {
        var welder = new VertexWelder(_tolerance.Value);
        var duplicates = 0;

        foreach (var vertex in mesh.Vertices)
        {
            var before = welder.Vertices.Count;
            _ = welder.AddOrGet(vertex);

            if (welder.Vertices.Count == before)
            {
                duplicates++;
            }
        }

        return duplicates;
    }
}

/// <summary>Ask for the connected components of a mesh, one mesh each.</summary>
public sealed record ComponentsRequest(IMesh Mesh);

/// <summary>
/// Components are found with a disjoint-set over vertex indices. Triangles are then
/// grouped by the root of their first corner, in first-appearance order so the
/// output is reproducible.
/// </summary>
internal sealed class ComponentsHandler
{
    public Result<ImmutableArray<IMesh>> Handle(ComponentsRequest request)
    {
        var mesh = request.Mesh;
        if (mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var parent = new int[mesh.VertexCount];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var offset = t * 3;
            Merge(parent, mesh.Triangles[offset], mesh.Triangles[offset + 1]);
            Merge(parent, mesh.Triangles[offset + 1], mesh.Triangles[offset + 2]);
        }

        var order = new List<int>();
        var buckets = new Dictionary<int, List<int>>();

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var root = Find(parent, mesh.Triangles[t * 3]);
            if (!buckets.TryGetValue(root, out var bucket))
            {
                bucket = [];
                buckets[root] = bucket;
                order.Add(root);
            }

            bucket.Add(t);
        }

        var components = ImmutableArray.CreateBuilder<IMesh>(order.Count);
        var index = 0;

        foreach (var root in order)
        {
            var extracted = Extract(mesh, buckets[root], index++);
            if (extracted.IsFailure)
            {
                return Result.Failure<ImmutableArray<IMesh>>(extracted.Error);
            }

            components.Add(extracted.Value);
        }

        return components.MoveToImmutable();
    }

    private static Result<IMesh> Extract(IMesh mesh, List<int> triangleIndices, int ordinal)
    {
        var remapped = new Dictionary<int, int>();
        var vertices = ImmutableArray.CreateBuilder<Vec3>();
        var triangles = ImmutableArray.CreateBuilder<int>(triangleIndices.Count * 3);

        foreach (var triangle in triangleIndices)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                var original = mesh.Triangles[(triangle * 3) + corner];
                if (!remapped.TryGetValue(original, out var mapped))
                {
                    mapped = vertices.Count;
                    remapped[original] = mapped;
                    vertices.Add(mesh.Vertices[original]);
                }

                triangles.Add(mapped);
            }
        }

        return ImmutableMesh.Create(
            vertices.ToImmutable(),
            triangles.MoveToImmutable(),
            mesh.Metadata.WithName($"{mesh.Metadata.Name} part {ordinal}"));
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Merge(int[] parent, int left, int right)
    {
        var a = Find(parent, left);
        var b = Find(parent, right);

        if (a != b)
        {
            parent[Math.Max(a, b)] = Math.Min(a, b);
        }
    }
}

/// <summary>Ask for the normal of every vertex.</summary>
public sealed record VertexNormalsRequest(IMesh Mesh);

/// <summary>
/// Each vertex takes the sum of the un-normalised normals of the faces around it. That cross
/// product is twice the face's area, so larger faces pull harder - the weighting renderers and
/// MeshLib's per-vertex normals both use, and the one lighting looks right with.
/// </summary>
internal sealed class VertexNormalsHandler
{
    public Result<ImmutableArray<Vec3>> Handle(VertexNormalsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        var mesh = request.Mesh;
        var accumulated = new Vec3[mesh.VertexCount];

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.TriangleAt(t);
            var faceNormal = (b - a).Cross(c - a);

            accumulated[mesh.Triangles[t * 3]] += faceNormal;
            accumulated[mesh.Triangles[(t * 3) + 1]] += faceNormal;
            accumulated[mesh.Triangles[(t * 3) + 2]] += faceNormal;
        }

        var normals = ImmutableArray.CreateBuilder<Vec3>(accumulated.Length);
        foreach (var sum in accumulated)
        {
            normals.Add(Internal.Spatial.MeshBvh.Normalise(sum));
        }

        return normals.MoveToImmutable();
    }
}

/// <summary>Ask how many triangles of a mesh pass through another of its triangles.</summary>
public sealed record SelfIntersectionsRequest(IMesh Mesh);

/// <summary>
/// Bounding-box overlap on a BVH is the broad phase and an exact triangle-triangle test the
/// narrow one, so this stays usable on the tens-of-thousands-of-triangle meshes validated on
/// import rather than being the n-squared test the definition implies. Pairs sharing a vertex
/// are adjacent geometry, not crossings, and are skipped.
/// </summary>
internal sealed class SelfIntersectionsHandler
{
    public Result<int> Handle(SelfIntersectionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        var mesh = request.Mesh;
        if (mesh.IsEmpty)
        {
            return 0;
        }

        var bvh = new Internal.Spatial.MeshBvh(mesh);
        var intersecting = new bool[mesh.TriangleCount];
        var triangles = mesh.Triangles;

        for (var i = 0; i < mesh.TriangleCount; i++)
        {
            var i0 = triangles[i * 3];
            var i1 = triangles[(i * 3) + 1];
            var i2 = triangles[(i * 3) + 2];
            var (a0, a1, a2) = mesh.TriangleAt(i);

            var self = i;
            bvh.QueryBox(a0.ComponentMin(a1).ComponentMin(a2), a0.ComponentMax(a1).ComponentMax(a2), candidate =>
            {
                // Each unordered pair is tested once.
                if (candidate <= self || (intersecting[self] && intersecting[candidate]))
                {
                    return;
                }

                var j0 = triangles[candidate * 3];
                var j1 = triangles[(candidate * 3) + 1];
                var j2 = triangles[(candidate * 3) + 2];

                if (i0 == j0 || i0 == j1 || i0 == j2 ||
                    i1 == j0 || i1 == j1 || i1 == j2 ||
                    i2 == j0 || i2 == j1 || i2 == j2)
                {
                    return;
                }

                var (b0, b1, b2) = mesh.TriangleAt(candidate);
                if (Internal.Spatial.TriangleIntersection.Intersects(a0, a1, a2, b0, b1, b2))
                {
                    intersecting[self] = true;
                    intersecting[candidate] = true;
                }
            });
        }

        return intersecting.Count(flag => flag);
    }
}

/// <summary>The <see cref="IGeometryEvaluators"/> facade over the evaluator slices.</summary>
internal sealed class GeometryEvaluators(Tolerance tolerance) : IGeometryEvaluators
{
    private readonly StatisticsHandler _statistics = new();
    private readonly TopologyHandler _topology = new(tolerance);
    private readonly ComponentsHandler _components = new();
    private readonly VertexNormalsHandler _normals = new();
    private readonly SelfIntersectionsHandler _selfIntersections = new();

    public Result<MeshStatistics> GetStatistics(IMesh mesh) => _statistics.Handle(new StatisticsRequest(mesh));

    public Result<TopologyValidation> ValidateTopology(IMesh mesh) => _topology.Handle(new TopologyRequest(mesh));

    public Result<ImmutableArray<IMesh>> SeparateComponents(IMesh mesh) => _components.Handle(new ComponentsRequest(mesh));

    public Result<ImmutableArray<Vec3>> ComputeVertexNormals(IMesh mesh) => _normals.Handle(new VertexNormalsRequest(mesh));

    public Result<int> CountSelfIntersections(IMesh mesh) => _selfIntersections.Handle(new SelfIntersectionsRequest(mesh));
}
