using System.Collections.Concurrent;

namespace GeometryEngine.Evaluators;

/// <summary>Ask for the local high points of a surface along a direction.</summary>
public sealed record PeaksRequest(IMesh Mesh, Direction Up, ImmutableArray<Vec3> VertexNormals);

/// <summary>
/// Peaks by topographic prominence. A level is swept down from the top of the surface; each peak
/// starts its own region of surface above the level, and when two regions meet, the one with the
/// lower peak has just spilled into the other - the distance from that peak down to the level is
/// its prominence. The elder rule, run over a union-find of vertices in descending height.
/// </summary>
internal sealed class PeaksHandler
{
    public Result<ISurfacePeaks> Handle(PeaksRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);
        ArgumentNullException.ThrowIfNull(request.Up);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var surface = WeldedSurface.Build(request.Mesh, request.VertexNormals, request.Up.Vector);
        var peaks = FindPeaks(surface)
            .Select(peak => new SurfacePeak(peak.Vertex, surface.Positions[peak.Vertex], surface.Normals[peak.Vertex], peak.Prominence))
            .OrderByDescending(peak => surface.Heights[peak.Id])
            .ThenBy(peak => peak.Id)
            .ToImmutableArray();

        return Result.Success<ISurfacePeaks>(new SurfacePeakMap(surface, peaks));
    }

    private static List<(int Vertex, double Prominence)> FindPeaks(WeldedSurface surface)
    {
        var count = surface.Positions.Length;

        // Descending height; ties broken by index so the sweep has one fixed order.
        var order = Enumerable.Range(0, count)
            .OrderByDescending(v => surface.Heights[v])
            .ThenBy(v => v)
            .ToArray();

        var rank = new int[count];
        for (var i = 0; i < order.Length; i++)
        {
            rank[order[i]] = i;
        }

        var parent = new int[count];
        var peakOf = new int[count];
        var visited = new bool[count];
        var peaks = new List<(int, double)>();

        int Find(int v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }

            return v;
        }

        foreach (var v in order)
        {
            visited[v] = true;
            parent[v] = v;
            peakOf[v] = v;

            var root = -1;
            foreach (var n in surface.Neighbours(v))
            {
                if (!visited[n])
                {
                    continue;
                }

                var other = Find(n);
                if (root == -1)
                {
                    root = other;
                    continue;
                }

                if (other == root)
                {
                    continue;
                }

                // Two regions meet at v: the one with the lower peak spills into the other here.
                var (elder, younger) = rank[peakOf[root]] < rank[peakOf[other]] ? (root, other) : (other, root);
                var spilled = peakOf[younger];
                peaks.Add((spilled, surface.Heights[spilled] - surface.Heights[v]));

                parent[younger] = elder;
                root = elder;
            }

            if (root != -1)
            {
                parent[v] = root;
            }

            // Otherwise nothing above v touches it: v is a peak, and starts its own region.
        }

        // Whatever is left never spilled anywhere: the top of each connected piece.
        for (var v = 0; v < count; v++)
        {
            if (Find(v) == v)
            {
                peaks.Add((peakOf[v], double.PositiveInfinity));
            }
        }

        return peaks;
    }
}

/// <summary>
/// The mesh as a graph of welded vertices, with each one's height along the up direction and its
/// coordinates across it.
/// </summary>
internal sealed class WeldedSurface
{
    public required Vec3[] Positions { get; init; }
    public required Vec3[] Normals { get; init; }
    public required double[] Heights { get; init; }
    public required Vec2[] Across { get; init; }
    public required Vec3 Up { get; init; }
    public required List<(int A, int B, int C)> Triangles { get; init; }

    // Compressed adjacency: the neighbours of v are _adjacency[_offsets[v] .. _offsets[v + 1]].
    private int[] _offsets = [];
    private int[] _adjacency = [];

    public ReadOnlySpan<int> Neighbours(int v) => _adjacency.AsSpan(_offsets[v], _offsets[v + 1] - _offsets[v]);

    public static WeldedSurface Build(IMesh mesh, ImmutableArray<Vec3> vertexNormals, Vec3 up)
    {
        var ids = new Dictionary<Vec3, int>();
        var remap = new int[mesh.VertexCount];
        var positions = new List<Vec3>();
        var normalSums = new List<Vec3>();

        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var position = mesh.Vertices[i];
            if (!ids.TryGetValue(position, out var id))
            {
                id = positions.Count;
                ids[position] = id;
                positions.Add(position);
                normalSums.Add(Vec3.Zero);
            }

            remap[i] = id;
            normalSums[id] += vertexNormals[i];
        }

        var triangles = new List<(int, int, int)>(mesh.TriangleCount);
        var degree = new int[positions.Count + 1];
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = remap[mesh.Triangles[t * 3]];
            var b = remap[mesh.Triangles[(t * 3) + 1]];
            var c = remap[mesh.Triangles[(t * 3) + 2]];
            if (a == b || b == c || c == a)
            {
                continue;
            }

            triangles.Add((a, b, c));
            degree[a] += 2;
            degree[b] += 2;
            degree[c] += 2;
        }

        // Each edge is listed once per triangle using it, so neighbours repeat - harmless for both
        // the sweep and the flood fill, and cheaper than removing them.
        var offsets = new int[positions.Count + 1];
        for (var v = 0; v < positions.Count; v++)
        {
            offsets[v + 1] = offsets[v] + degree[v];
        }

        var adjacency = new int[offsets[^1]];
        var fill = (int[])offsets.Clone();
        foreach (var (a, b, c) in triangles)
        {
            adjacency[fill[a]++] = b;
            adjacency[fill[a]++] = c;
            adjacency[fill[b]++] = c;
            adjacency[fill[b]++] = a;
            adjacency[fill[c]++] = a;
            adjacency[fill[c]++] = b;
        }

        // Any two directions across the up axis will do for centring a summit; the least aligned
        // world axis gives the steadiest pair.
        var reference = Math.Abs(up.X) < 0.6 ? Vec3.UnitX : Vec3.UnitY;
        var u = up.Cross(reference).Normalize();
        var w = up.Cross(u);

        return new WeldedSurface
        {
            Positions = [.. positions],
            Normals = [.. normalSums.Select(n => n.LengthSquared > 0 ? n.Normalize() : Vec3.Zero)],
            Heights = [.. positions.Select(p => p.Dot(up))],
            Across = [.. positions.Select(p => new Vec2(p.Dot(u), p.Dot(w)))],
            Up = up,
            Triangles = triangles,
            _offsets = offsets,
            _adjacency = adjacency,
        };
    }

    /// <summary>The vertices joined to <paramref name="start"/> through surface no lower than <paramref name="floor"/>.</summary>
    public HashSet<int> RegionAbove(int start, double floor)
    {
        var region = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            foreach (var n in Neighbours(v))
            {
                if (Heights[n] >= floor && region.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }

        return region;
    }

    public int NearestVertex(Vec3 point)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var v = 0; v < Positions.Length; v++)
        {
            var distance = Positions[v].DistanceSquared(point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = v;
            }
        }

        return best;
    }
}

internal sealed class SurfacePeakMap(WeldedSurface surface, ImmutableArray<SurfacePeak> peaks) : ISurfacePeaks
{
    // Callers ask about the same few points - existing channels - for peak after peak.
    private readonly ConcurrentDictionary<Vec3, int> _nearest = new();

    public ImmutableArray<SurfacePeak> Peaks { get; } = peaks;

    public ImmutableArray<bool> WithinReach(SurfacePeak peak, double depth, ImmutableArray<Vec3> points)
    {
        if (points.IsDefaultOrEmpty)
        {
            return [];
        }

        var cap = surface.RegionAbove(peak.Id, surface.Heights[peak.Id] - depth);
        return [.. points.Select(point => cap.Contains(_nearest.GetOrAdd(point, surface.NearestVertex)))];
    }

    public (Vec3 Point, Vec3 Normal) Summit(SurfacePeak peak, double tolerance)
    {
        var top = surface.Heights[peak.Id];
        var fallback = (surface.Positions[peak.Id], surface.Normals[peak.Id]);

        var inSummit = surface.RegionAbove(peak.Id, top - tolerance);
        if (inSummit.Count < 3)
        {
            return fallback;
        }

        var centre = new Vec2(
            inSummit.Average(v => surface.Across[v].X),
            inSummit.Average(v => surface.Across[v].Y));

        foreach (var (a, b, c) in surface.Triangles)
        {
            if (!inSummit.Contains(a) || !inSummit.Contains(b) || !inSummit.Contains(c))
            {
                continue;
            }

            if (!Barycentric(surface.Across[a], surface.Across[b], surface.Across[c], centre, out var wa, out var wb, out var wc))
            {
                continue;
            }

            var pa = surface.Positions[a];
            var pb = surface.Positions[b];
            var pc = surface.Positions[c];
            var point = (pa * wa) + (pb * wb) + (pc * wc);
            var normal = (pb - pa).Cross(pc - pa);

            // A summit shaped like a ring has its middle over the hole; the triangle under the
            // centre then faces down or sits well below the top, and the peak is the better spot.
            if (normal.Dot(surface.Up) <= 0 || point.Dot(surface.Up) < top - tolerance)
            {
                continue;
            }

            return (point, normal.Normalize());
        }

        return fallback;
    }

    // Barycentric coordinates of p in the triangle's shadow across the up axis; false when outside it.
    private static bool Barycentric(Vec2 a, Vec2 b, Vec2 c, Vec2 p, out double wa, out double wb, out double wc)
    {
        wa = wb = wc = 0;

        var area = (b - a).Cross(c - a);
        if (Math.Abs(area) < 1e-12)
        {
            return false;
        }

        wb = (p - a).Cross(c - a) / area;
        wc = (b - a).Cross(p - a) / area;
        wa = 1 - wb - wc;

        const double slack = -1e-9;
        return wa >= slack && wb >= slack && wc >= slack;
    }
}
