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

        // Read once: through the interface each of these is a call, and the loop below would
        // make it three times a triangle.
        var triangles = mesh.Triangles.AsSpan();
        var vertices = mesh.Vertices.AsSpan();

        var shells = new DisjointSet(vertices.Length);
        var referenced = new bool[vertices.Length];
        var degenerate = 0;

        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);

            // Connectivity for the shell count spans every triangle, degenerate or not.
            shells.Union(a, b);
            shells.Union(b, c);
            referenced[a] = referenced[b] = referenced[c] = true;

            // A triangle whose corners share an index has no edges to pair, so it is left out of
            // the edge tally. A sliver - distinct corners, no area - is not: it is still part of the
            // surface's connectivity, and a closed boolean result routinely carries them where one
            // operand's vertex lands on another's edge. Dropping its edges would report holes in a
            // surface that has none, and every volume computed downstream would read as zero.
            if (a == b || b == c || c == a || IsSliver(vertices[a], vertices[b], vertices[c]))
            {
                degenerate++;
            }
        }

        // Only vertices a triangle uses are ever joined, so each shell's root is one of them, and
        // the vertices no triangle uses are the rest.
        var (shellCount, unreferenced) = (0, 0);
        for (var v = 0; v < referenced.Length; v++)
        {
            if (!referenced[v])
            {
                unreferenced++;
            }
            else if (shells.Find(v) == v)
            {
                shellCount++;
            }
        }

        var edges = TopologyTallies.Edges(triangles, vertices.Length);

        return new TopologyValidation(
            edges.Boundary,
            edges.NonManifold,
            degenerate,
            CountDuplicateVertices(vertices),
            edges.InconsistentWinding,
            TopologyTallies.DuplicateFaces(triangles, vertices.Length),
            shellCount)
        {
            EdgeCount = edges.Edges,
            UnreferencedVertexCount = unreferenced,
        };
    }

    private bool IsSliver(Vec3 a, Vec3 b, Vec3 c) =>
        (b - a).Cross(c - a).Length * 0.5 <= _tolerance.Value * _tolerance.Value;

    private int CountDuplicateVertices(ReadOnlySpan<Vec3> vertices)
    {
        var welder = new VertexWelder(_tolerance.Value);
        var duplicates = 0;

        foreach (var vertex in vertices)
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
            mesh.Metadata
                .CarriedThrough(MeshOperation.Rebuild)
                .WithName($"{mesh.Metadata.Name} part {ordinal}"));
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
        var candidates = new List<int>();

        for (var i = 0; i < mesh.TriangleCount; i++)
        {
            var i0 = triangles[i * 3];
            var i1 = triangles[(i * 3) + 1];
            var i2 = triangles[(i * 3) + 2];
            var (a0, a1, a2) = mesh.TriangleAt(i);

            // One list reused across every triangle, rather than a stack, closure and delegate
            // allocated per query. Candidates come back in the order the callback form visited them.
            bvh.QueryBox(a0.ComponentMin(a1).ComponentMin(a2), a0.ComponentMax(a1).ComponentMax(a2), candidates);
            foreach (var candidate in candidates)
            {
                // Each unordered pair is tested once.
                if (candidate <= i || (intersecting[i] && intersecting[candidate]))
                {
                    continue;
                }

                var j0 = triangles[candidate * 3];
                var j1 = triangles[(candidate * 3) + 1];
                var j2 = triangles[(candidate * 3) + 2];

                if (i0 == j0 || i0 == j1 || i0 == j2 ||
                    i1 == j0 || i1 == j1 || i1 == j2 ||
                    i2 == j0 || i2 == j1 || i2 == j2)
                {
                    continue;
                }

                var (b0, b1, b2) = mesh.TriangleAt(candidate);
                if (Internal.Spatial.TriangleIntersection.Intersects(a0, a1, a2, b0, b1, b2))
                {
                    intersecting[i] = true;
                    intersecting[candidate] = true;
                }
            }
        }

        return intersecting.Count(flag => flag);
    }
}

/// <summary>Ask how far one mesh's vertices lie from another's surface.</summary>
public sealed record DeviationRequest(IMesh Mesh, IMesh Reference);

/// <summary>
/// One batch of signed-distance queries against the reference, answered natively and in
/// parallel where the native field is present. The reference's own index is used, so comparing
/// mesh after mesh against one reference - a heatmap redrawn as smoothing is tuned - builds it,
/// and its native field, once.
/// </summary>
internal sealed class DeviationHandler
{
    public Result<SurfaceDeviation> Handle(DeviationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);
        ArgumentNullException.ThrowIfNull(request.Reference);

        if (request.Mesh.IsEmpty || request.Reference.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var distances = Spatial.SharedIndexes.For(request.Reference).SignedDistances(request.Mesh.Vertices);

        double outside = 0, inside = 0, absolute = 0, squares = 0;
        foreach (var distance in distances)
        {
            outside = Math.Max(outside, distance);
            inside = Math.Max(inside, -distance);
            absolute += Math.Abs(distance);
            squares += distance * distance;
        }

        return new SurfaceDeviation(
            distances,
            outside,
            inside,
            absolute / distances.Length,
            Math.Sqrt(squares / distances.Length));
    }
}

/// <summary>
/// The <see cref="IGeometryEvaluators"/> facade over the evaluator slices. Statistics, topology
/// and normals are read from the mesh's measurement cache when it has them and written to it when
/// it does not, so the handlers stay pure measurements and the caching lives in one place.
/// Failures are not cached: the inputs that fail are empty meshes, which are cheap to refuse again.
/// </summary>
internal sealed class GeometryEvaluators(Tolerance tolerance) : IGeometryEvaluators
{
    private readonly StatisticsHandler _statistics = new();
    private readonly TopologyHandler _topology = new(tolerance);
    private readonly ComponentsHandler _components = new();
    private readonly VertexNormalsHandler _normals = new();
    private readonly SelfIntersectionsHandler _selfIntersections = new();
    private readonly DeviationHandler _deviation = new();
    private readonly PeaksHandler _peaks = new();

    public Result<ISurfacePeaks> FindPeaks(IMesh mesh, Direction up)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        // Through this facade rather than the handler, so a mesh whose normals were already
        // measured - one about to be drawn, say - does not have them computed twice.
        var normals = ComputeVertexNormals(mesh);
        return normals.IsFailure
            ? Result.Failure<ISurfacePeaks>(normals.Error)
            : _peaks.Handle(new PeaksRequest(mesh, up, normals.Value));
    }

    public Result<SurfaceDeviation> MeasureDeviation(IMesh mesh, IMesh reference) =>
        _deviation.Handle(new DeviationRequest(mesh, reference));

    public Result<MeshStatistics> GetStatistics(IMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var cache = (mesh as ImmutableMesh)?.Measurements;
        if (cache?.Statistics is { } known)
        {
            return known;
        }

        var measured = _statistics.Handle(new StatisticsRequest(mesh));
        if (measured.IsSuccess && cache is not null)
        {
            cache.Statistics = measured.Value;
        }

        return measured;
    }

    public Result<TopologyValidation> ValidateTopology(IMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var cache = (mesh as ImmutableMesh)?.Measurements;
        if (cache?.TopologyAt(tolerance.Value) is { } known)
        {
            return known;
        }

        var measured = _topology.Handle(new TopologyRequest(mesh));
        if (measured.IsSuccess && cache is not null)
        {
            cache.SetTopology(tolerance.Value, measured.Value);
        }

        return measured;
    }

    public Result<ImmutableArray<IMesh>> SeparateComponents(IMesh mesh) => _components.Handle(new ComponentsRequest(mesh));

    public Result<ImmutableArray<Vec3>> ComputeVertexNormals(IMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var cache = (mesh as ImmutableMesh)?.Measurements;
        if (cache?.VertexNormals is { } known)
        {
            return known;
        }

        var measured = _normals.Handle(new VertexNormalsRequest(mesh));
        if (measured.IsSuccess && cache is not null)
        {
            cache.VertexNormals = measured.Value;
        }

        return measured;
    }

    public Result<int> CountSelfIntersections(IMesh mesh) => _selfIntersections.Handle(new SelfIntersectionsRequest(mesh));
}
