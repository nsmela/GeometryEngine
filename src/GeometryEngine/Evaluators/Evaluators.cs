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

            if (a == b || b == c || c == a || IsSliver(mesh, a, b, c))
            {
                degenerate++;
                continue;
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

        return new TopologyValidation(
            boundary,
            nonManifold,
            degenerate,
            CountDuplicateVertices(mesh),
            inconsistentWinding,
            duplicateFaces,
            shells.CountRootsAmong(mesh.Triangles));
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

/// <summary>The <see cref="IGeometryEvaluators"/> facade over the evaluator slices.</summary>
internal sealed class GeometryEvaluators(Tolerance tolerance) : IGeometryEvaluators
{
    private readonly StatisticsHandler _statistics = new();
    private readonly TopologyHandler _topology = new(tolerance);
    private readonly ComponentsHandler _components = new();

    public Result<MeshStatistics> GetStatistics(IMesh mesh) => _statistics.Handle(new StatisticsRequest(mesh));

    public Result<TopologyValidation> ValidateTopology(IMesh mesh) => _topology.Handle(new TopologyRequest(mesh));

    public Result<ImmutableArray<IMesh>> SeparateComponents(IMesh mesh) => _components.Handle(new ComponentsRequest(mesh));
}
