namespace GeometryEngine.Internal.Spatial;

/// <summary>Which part of a triangle a closest point landed on.</summary>
internal enum TriangleFeature
{
    Face,
    VertexA,
    VertexB,
    VertexC,
    EdgeAB,
    EdgeBC,
    EdgeCA,
}

/// <summary>
/// A bounding-volume hierarchy over a mesh's triangles: ray intersection, closest point,
/// signed distance, and box overlap. The managed counterpart of the native distance field, and
/// the only one where that library is absent.
///
/// Immutable once built apart from the lazily built pseudonormal tables, which are published
/// atomically, so one tree can be queried from many threads - which Manifold's level-set
/// mesher does.
/// </summary>
internal sealed class MeshBvh
{
    /// <summary>Leaves hold at most this many triangles; below it the traversal costs more than the scan.</summary>
    private const int LeafSize = 8;

    private readonly ImmutableArray<Vec3> _vertices;
    private readonly ImmutableArray<int> _triangles;
    private readonly int[] _triangleOrder;
    private readonly Node[] _nodes;
    private readonly int _nodeCount;
    private readonly int _maxDepth;

    private readonly object _pseudoNormalLock = new();
    private volatile PseudoNormals? _pseudoNormals;

    /// <summary>
    /// Traversal scratch space. Every query is a stack-driven descent, and allocating a stack per
    /// query dominated the offset cost - the level-set mesher issues one query per sample. It is
    /// thread-static because Manifold calls the distance callback from several threads at once.
    /// </summary>
    [ThreadStatic]
    private static int[]? _traversalStack;

    private struct Node
    {
        public Vec3 Min;
        public Vec3 Max;
        public int Start;
        public int Count;
        public int Left;
        public int Right;
    }

    private sealed record PseudoNormals(
        Vec3[] Faces,
        int[] Canonical,
        Vec3[] Vertices,
        Dictionary<(int, int), Vec3> Edges);

    public MeshBvh(IMesh mesh)
    {
        _vertices = mesh.Vertices;
        _triangles = mesh.Triangles;

        var triangleCount = _triangles.Length / 3;
        _triangleOrder = new int[triangleCount];
        for (var i = 0; i < triangleCount; i++)
        {
            _triangleOrder[i] = i;
        }

        // A binary tree over n leaves of at least one triangle needs fewer than 2n nodes.
        _nodes = new Node[Math.Max(1, triangleCount * 2)];
        if (triangleCount > 0)
        {
            Build(0, triangleCount, ref _nodeCount, 1, ref _maxDepth);
        }
    }

    /// <summary>
    /// Upper bound on a traversal stack. A descent pops one node and pushes its two children, so
    /// it never holds more than one entry per level, plus the root.
    /// </summary>
    private int StackDepth => (_maxDepth * 2) + 2;

    public bool IsEmpty => _nodeCount == 0;

    private static int[] RentStack(int depth)
    {
        if (_traversalStack is null || _traversalStack.Length < depth)
        {
            _traversalStack = new int[Math.Max(64, depth)];
        }

        return _traversalStack;
    }

    private int Build(int start, int count, ref int nodeCount, int depth, ref int maxDepth)
    {
        var nodeIndex = nodeCount++;
        var node = new Node { Start = start, Count = count, Left = -1, Right = -1 };

        if (depth > maxDepth)
        {
            maxDepth = depth;
        }

        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        for (var i = start; i < start + count; i++)
        {
            var (a, b, c) = Triangle(_triangleOrder[i]);
            min = min.ComponentMin(a).ComponentMin(b).ComponentMin(c);
            max = max.ComponentMax(a).ComponentMax(b).ComponentMax(c);
        }

        node.Min = min;
        node.Max = max;

        if (count > LeafSize)
        {
            // Split down the middle of the widest axis - cheap, and good enough for meshes that
            // are already spatially coherent, which scanned and generated meshes are.
            var extent = max - min;
            var axis = extent.X > extent.Y ? (extent.X > extent.Z ? 0 : 2) : (extent.Y > extent.Z ? 1 : 2);
            var split = Axis(min + (extent * 0.5), axis);

            var mid = Partition(start, count, axis, split);
            if (mid == start || mid == start + count)
            {
                mid = start + (count / 2); // Every centroid coincides: halve by count instead.
            }

            node.Left = Build(start, mid - start, ref nodeCount, depth + 1, ref maxDepth);
            node.Right = Build(mid, start + count - mid, ref nodeCount, depth + 1, ref maxDepth);
            node.Count = 0;
        }

        _nodes[nodeIndex] = node;
        return nodeIndex;
    }

    private int Partition(int start, int count, int axis, double split)
    {
        var i = start;
        var j = start + count - 1;
        while (i <= j)
        {
            var (a, b, c) = Triangle(_triangleOrder[i]);
            if (Axis((a + b + c) / 3.0, axis) < split)
            {
                i++;
            }
            else
            {
                (_triangleOrder[i], _triangleOrder[j]) = (_triangleOrder[j], _triangleOrder[i]);
                j--;
            }
        }

        return i;
    }

    private static double Axis(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    public (Vec3 A, Vec3 B, Vec3 C) Triangle(int triangle) => (
        _vertices[_triangles[triangle * 3]],
        _vertices[_triangles[(triangle * 3) + 1]],
        _vertices[_triangles[(triangle * 3) + 2]]);

    /// <summary>Geometric unit normal of a triangle, or zero if it is degenerate.</summary>
    public Vec3 TriangleNormal(int triangle)
    {
        var (a, b, c) = Triangle(triangle);
        return Normalise((b - a).Cross(c - a));
    }

    /// <summary>
    /// Closest hit of a ray in front of its origin. <paramref name="direction"/> must be a unit
    /// vector.
    /// </summary>
    public bool Raycast(Vec3 origin, Vec3 direction, out double distance, out int triangle)
    {
        distance = double.MaxValue;
        triangle = -1;
        if (IsEmpty)
        {
            return false;
        }

        var inverse = new Vec3(
            1.0 / (direction.X == 0 ? 1e-300 : direction.X),
            1.0 / (direction.Y == 0 ? 1e-300 : direction.Y),
            1.0 / (direction.Z == 0 ? 1e-300 : direction.Z));

        var stack = RentStack(StackDepth);
        var top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref var node = ref _nodes[stack[--top]];
            if (!IntersectsBox(origin, inverse, node.Min, node.Max, distance))
            {
                continue;
            }

            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var candidate = _triangleOrder[i];
                    var (a, b, c) = Triangle(candidate);
                    if (RayTriangle(origin, direction, a, b, c, out var t) && t < distance)
                    {
                        distance = t;
                        triangle = candidate;
                    }
                }

                continue;
            }

            stack[top++] = node.Left;
            stack[top++] = node.Right;
        }

        return triangle >= 0;
    }

    /// <summary>Nearest point on the surface to <paramref name="point"/>, and the triangle carrying it.</summary>
    public bool ClosestPoint(Vec3 point, out Vec3 closest, out int triangle, out double distance) =>
        ClosestPoint(point, out closest, out triangle, out distance, out _);

    private bool ClosestPoint(Vec3 point, out Vec3 closest, out int triangle, out double distance, out TriangleFeature feature)
    {
        closest = point;
        triangle = -1;
        distance = double.MaxValue;
        feature = TriangleFeature.Face;
        if (IsEmpty)
        {
            return false;
        }

        var bestSquared = double.MaxValue;
        var stack = RentStack(StackDepth);
        var top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref var node = ref _nodes[stack[--top]];

            // Nothing inside a box can beat a hit closer than the box itself.
            if (SquaredDistanceToBox(point, node.Min, node.Max) > bestSquared)
            {
                continue;
            }

            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var candidate = _triangleOrder[i];
                    var (a, b, c) = Triangle(candidate);
                    var projected = ClosestPointOnTriangle(point, a, b, c, out var candidateFeature);
                    var squared = (projected - point).LengthSquared;
                    if (squared < bestSquared)
                    {
                        bestSquared = squared;
                        closest = projected;
                        triangle = candidate;
                        feature = candidateFeature;
                    }
                }

                continue;
            }

            stack[top++] = node.Left;
            stack[top++] = node.Right;
        }

        distance = Math.Sqrt(bestSquared);
        return triangle >= 0;
    }

    /// <summary>
    /// Distance to the surface, negative inside the solid.
    ///
    /// The sign is taken against the angle-weighted pseudonormal of whichever feature of the
    /// nearest triangle the closest point actually landed on - its interior, an edge, or a corner
    /// (Baerentzen and Aanaes, 2005). Testing against the triangle's own face normal is only right
    /// when the closest point is in the face interior, and most of the volume around a crease is
    /// nearest to an edge or a corner. There the face normal belongs to whichever neighbour won the
    /// closest-point search, the sign flips essentially at random, and an isosurface built on the
    /// field comes back shredded into islands.
    /// </summary>
    public double SignedDistance(Vec3 point)
    {
        if (!ClosestPoint(point, out var closest, out var triangle, out var distance, out var feature))
        {
            return double.MaxValue;
        }

        var normal = PseudoNormal(triangle, feature);
        if (normal == Vec3.Zero)
        {
            return distance;
        }

        return (point - closest).Dot(normal) < 0 ? -distance : distance;
    }

    /// <summary>
    /// Calls <paramref name="onOverlap"/> for every triangle whose bounds overlap the given box:
    /// the broad phase of self-intersection testing.
    /// </summary>
    public void QueryBox(Vec3 min, Vec3 max, Action<int> onOverlap)
    {
        if (IsEmpty)
        {
            return;
        }

        // Its own stack, not the pooled one: this is the only traversal that hands control back
        // to a caller mid-descent, and a callback that queried the tree again would walk over
        // the shared buffer underneath it.
        var stack = new int[StackDepth];
        var top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref var node = ref _nodes[stack[--top]];

            if (node.Min.X > max.X || node.Max.X < min.X ||
                node.Min.Y > max.Y || node.Max.Y < min.Y ||
                node.Min.Z > max.Z || node.Max.Z < min.Z)
            {
                continue;
            }

            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    onOverlap(_triangleOrder[i]);
                }

                continue;
            }

            stack[top++] = node.Left;
            stack[top++] = node.Right;
        }
    }

    /// <summary>
    /// The outward normal to test a point's side against, for the feature it is nearest. A face
    /// uses its own normal; an edge sums the two triangles sharing it; a vertex sums the triangles
    /// around it weighted by the angle each spans there, which keeps the sign continuous as the
    /// closest point crosses from one feature to the next.
    /// </summary>
    private Vec3 PseudoNormal(int triangle, TriangleFeature feature)
    {
        var tables = EnsurePseudoNormals();
        if (feature == TriangleFeature.Face)
        {
            return tables.Faces[triangle];
        }

        var a = tables.Canonical[_triangles[triangle * 3]];
        var b = tables.Canonical[_triangles[(triangle * 3) + 1]];
        var c = tables.Canonical[_triangles[(triangle * 3) + 2]];

        return feature switch
        {
            TriangleFeature.VertexA => Normalise(tables.Vertices[a]),
            TriangleFeature.VertexB => Normalise(tables.Vertices[b]),
            TriangleFeature.VertexC => Normalise(tables.Vertices[c]),
            TriangleFeature.EdgeAB => EdgeNormal(tables, a, b, triangle),
            TriangleFeature.EdgeBC => EdgeNormal(tables, b, c, triangle),
            TriangleFeature.EdgeCA => EdgeNormal(tables, c, a, triangle),
            _ => tables.Faces[triangle],
        };
    }

    private static Vec3 EdgeNormal(PseudoNormals tables, int first, int second, int triangle)
    {
        var key = first < second ? (first, second) : (second, first);

        // A boundary edge has no partner; the one face it has is all there is to go on.
        return tables.Edges.TryGetValue(key, out var normal) ? Normalise(normal) : tables.Faces[triangle];
    }

    /// <summary>
    /// Builds the pseudonormal tables on first use. Only signed distance needs them, and most
    /// trees are built for raycasting alone.
    /// </summary>
    private PseudoNormals EnsurePseudoNormals()
    {
        var existing = _pseudoNormals;
        if (existing is not null)
        {
            return existing;
        }

        lock (_pseudoNormalLock)
        {
            if (_pseudoNormals is not null)
            {
                return _pseudoNormals;
            }

            var triangleCount = _triangles.Length / 3;

            // Vertex and edge adjacency only mean anything on an indexed mesh. Canonical ids stand
            // in for a weld, so an unwelded soup still gets continuous normals, without disturbing
            // the positions the tree was built over.
            var weld = WeldTolerance();
            var canonical = new int[_vertices.Length];
            var lookup = new Dictionary<(long, long, long), int>(_vertices.Length);
            for (var i = 0; i < _vertices.Length; i++)
            {
                var v = _vertices[i];
                var key = ((long)Math.Round(v.X / weld), (long)Math.Round(v.Y / weld), (long)Math.Round(v.Z / weld));
                if (!lookup.TryGetValue(key, out var id))
                {
                    id = lookup.Count;
                    lookup[key] = id;
                }

                canonical[i] = id;
            }

            var faces = new Vec3[triangleCount];
            var vertexNormals = new Vec3[lookup.Count];
            var edgeNormals = new Dictionary<(int, int), Vec3>(triangleCount * 3 / 2);

            for (var t = 0; t < triangleCount; t++)
            {
                var (pa, pb, pc) = Triangle(t);
                var normal = Normalise((pb - pa).Cross(pc - pa));
                faces[t] = normal;
                if (normal == Vec3.Zero)
                {
                    continue;
                }

                var a = canonical[_triangles[t * 3]];
                var b = canonical[_triangles[(t * 3) + 1]];
                var c = canonical[_triangles[(t * 3) + 2]];

                // Weighting by the angle each triangle spans at a corner is what makes the vertex
                // normal independent of how finely the surface around it is tessellated.
                vertexNormals[a] += normal * Angle(pb - pa, pc - pa);
                vertexNormals[b] += normal * Angle(pa - pb, pc - pb);
                vertexNormals[c] += normal * Angle(pa - pc, pb - pc);

                AddEdgeNormal(edgeNormals, a, b, normal);
                AddEdgeNormal(edgeNormals, b, c, normal);
                AddEdgeNormal(edgeNormals, c, a, normal);
            }

            _pseudoNormals = new PseudoNormals(faces, canonical, vertexNormals, edgeNormals);
            return _pseudoNormals;
        }
    }

    /// <summary>A small fraction of the bounding diagonal: above float rounding, below any real feature.</summary>
    private double WeldTolerance()
    {
        ref var root = ref _nodes[0];
        return Math.Max((root.Max - root.Min).Length * 1e-7, 1e-12);
    }

    private static void AddEdgeNormal(Dictionary<(int, int), Vec3> edges, int a, int b, Vec3 normal)
    {
        var key = a < b ? (a, b) : (b, a);
        edges[key] = edges.TryGetValue(key, out var existing) ? existing + normal : normal;
    }

    private static double Angle(Vec3 first, Vec3 second)
    {
        var lengths = first.Length * second.Length;
        return lengths < 1e-300 ? 0 : Math.Acos(Math.Clamp(first.Dot(second) / lengths, -1, 1));
    }

    public static Vec3 Normalise(Vec3 v)
    {
        var length = v.Length;
        return length > 1e-300 ? v / length : Vec3.Zero;
    }

    private static bool IntersectsBox(Vec3 origin, Vec3 inverse, Vec3 min, Vec3 max, double maxDistance)
    {
        var t1 = (min.X - origin.X) * inverse.X;
        var t2 = (max.X - origin.X) * inverse.X;
        var tMin = Math.Min(t1, t2);
        var tMax = Math.Max(t1, t2);

        t1 = (min.Y - origin.Y) * inverse.Y;
        t2 = (max.Y - origin.Y) * inverse.Y;
        tMin = Math.Max(tMin, Math.Min(t1, t2));
        tMax = Math.Min(tMax, Math.Max(t1, t2));

        t1 = (min.Z - origin.Z) * inverse.Z;
        t2 = (max.Z - origin.Z) * inverse.Z;
        tMin = Math.Max(tMin, Math.Min(t1, t2));
        tMax = Math.Min(tMax, Math.Max(t1, t2));

        return tMax >= Math.Max(tMin, 0) && tMin <= maxDistance;
    }

    private static double SquaredDistanceToBox(Vec3 point, Vec3 min, Vec3 max)
    {
        var dx = Math.Max(Math.Max(min.X - point.X, 0), point.X - max.X);
        var dy = Math.Max(Math.Max(min.Y - point.Y, 0), point.Y - max.Y);
        var dz = Math.Max(Math.Max(min.Z - point.Z, 0), point.Z - max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>
    /// Moller-Trumbore. The barycentric test is slightly loose so a ray through a shared edge
    /// cannot slip between the two triangles either side of it.
    /// </summary>
    private static bool RayTriangle(Vec3 origin, Vec3 direction, Vec3 v0, Vec3 v1, Vec3 v2, out double t)
    {
        const double eps = 1e-12;
        const double tolerance = 1e-4;
        t = 0;

        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = direction.Cross(edge2);
        var a = edge1.Dot(h);
        if (a > -eps && a < eps)
        {
            return false;
        }

        var f = 1.0 / a;
        var s = origin - v0;
        var u = f * s.Dot(h);
        if (u < -tolerance || u > 1 + tolerance)
        {
            return false;
        }

        var q = s.Cross(edge1);
        var v = f * direction.Dot(q);
        if (v < -tolerance || u + v > 1 + tolerance)
        {
            return false;
        }

        t = f * edge2.Dot(q);
        return t > 1e-9;
    }

    /// <summary>
    /// Ericson, Real-Time Collision Detection: walk the Voronoi regions of the triangle's corners
    /// and edges before falling through to its interior. Each region is exactly the feature the
    /// pseudonormal has to come from, so it is reported alongside.
    /// </summary>
    private static Vec3 ClosestPointOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c, out TriangleFeature feature)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;

        var d1 = ab.Dot(ap);
        var d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0)
        {
            feature = TriangleFeature.VertexA;
            return a;
        }

        var bp = p - b;
        var d3 = ab.Dot(bp);
        var d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3)
        {
            feature = TriangleFeature.VertexB;
            return b;
        }

        var vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            var denominator = d1 - d3;
            feature = TriangleFeature.EdgeAB;
            return a + (ab * (Math.Abs(denominator) > 1e-300 ? d1 / denominator : 0));
        }

        var cp = p - c;
        var d5 = ab.Dot(cp);
        var d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6)
        {
            feature = TriangleFeature.VertexC;
            return c;
        }

        var vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            var denominator = d2 - d6;
            feature = TriangleFeature.EdgeCA;
            return a + (ac * (Math.Abs(denominator) > 1e-300 ? d2 / denominator : 0));
        }

        var va = (d3 * d6) - (d5 * d4);
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            var denominator = d4 - d3 + d5 - d6;
            feature = TriangleFeature.EdgeBC;
            return b + ((c - b) * (Math.Abs(denominator) > 1e-300 ? (d4 - d3) / denominator : 0));
        }

        var total = va + vb + vc;
        if (Math.Abs(total) < 1e-300)
        {
            feature = TriangleFeature.VertexA;
            return a;
        }

        feature = TriangleFeature.Face;
        return a + (ab * (vb / total)) + (ac * (vc / total));
    }
}
