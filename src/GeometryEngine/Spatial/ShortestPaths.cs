using System.Runtime.CompilerServices;

namespace GeometryEngine.Spatial;

internal static class SurfacePathErrors
{
    public static readonly Error OffSurface = new("Spatial.OffSurface", "A point could not be placed on the surface.");

    public static readonly Error NoPath = new("Spatial.NoSurfacePath", "The two points are on parts of the surface that do not join.");
}

/// <summary>Ask for the shortest way across a surface between two points.</summary>
public sealed record ShortestPathRequest(IMesh Mesh, Vec3 From, Vec3 To);

/// <summary>
/// Finds the shortest way across a surface: a band of faces joining the two ends, that band unfolded
/// flat and the path pulled taut through it, and then the band moved wherever it holds the path on
/// the wrong side of one of its vertices.
/// </summary>
/// <remarks>
/// <para>
/// The band starts as the straight line between the two ends dropped onto the surface, the faces
/// under it joined up by searching between them. On a convex body that line already lies over the
/// shortest path, and where it does not, it is a far closer start than a search across the whole
/// distance: a search over faces steps from centroid to centroid, and on a fine regular mesh that
/// finds a staircase whose corners the steps below then have to take out one by one.
/// </para>
/// <para>
/// The band decides the route and the funnel the path along it, and only within it. Two things go
/// wrong with that, and both are the band passing a vertex on the wrong side. Where the taut path
/// would leave the band it bends round one of the band's vertices instead - and a path pulled tight
/// across a surface never bends at a vertex whose faces close round it in less than a full turn, as
/// every vertex on a convex body does. And a band can run round the far side of a vertex the whole
/// way, giving a path that is straight and still not the shortest: over a cube's corner by the side
/// face rather than straight across the edge. So every vertex the band's edges meet is tried the
/// other way round, through the faces on its far side, the vertices the path turns at first, and a
/// change is kept when it shortens the path. Every change kept shortens it, so this ends.
/// </para>
/// <para>
/// Pulling the path taut is the funnel algorithm (Lee and Preparata; Mononen's form of it). It
/// works in the plane, which the band is made into by laying each face down beside the last
/// across the edge they share - so lengths along the path are true surface lengths, and a point
/// on a shared edge maps back to the surface by how far along that edge it lies.
/// </para>
/// </remarks>
internal sealed class ShortestPathHandler
{
    public Result<ImmutableArray<Vec3>> Handle(ShortestPathRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        var mesh = request.Mesh;
        if (mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var index = SharedIndexes.For(mesh);
        var start = index.ClosestPoint(request.From);
        var end = index.ClosestPoint(request.To);
        if (!start.HasValue || !end.HasValue)
        {
            return SurfacePathErrors.OffSurface;
        }

        var from = start.Value;
        var to = end.Value;
        if (from.Triangle == to.Triangle)
        {
            return ImmutableArray.Create(from.Point, to.Point);
        }

        var faces = FaceAdjacency.For(mesh);
        var corridor = faces.Seed(index, from, to);
        if (corridor is null)
        {
            return SurfacePathErrors.NoPath;
        }

        var best = Funnel.Pull(faces, corridor, from.Point, to.Point);
        for (var round = 0; round < MaxReroutes; round++)
        {
            var improved = false;
            foreach (var (portal, vertex, turns) in best.Pivots)
            {
                var rerouted = faces.AroundTheOtherSide(corridor, portal, vertex, shorterWayOnly: !turns);
                if (rerouted is null)
                {
                    continue;
                }

                var attempt = Funnel.Pull(faces, rerouted, from.Point, to.Point);
                if (attempt.Length < best.Length - 1e-12)
                {
                    (corridor, best, improved) = (rerouted, attempt, true);
                    break;
                }
            }

            if (!improved)
            {
                break;
            }
        }

        return best.Points;
    }

    /// <summary>
    /// How many times the band may be moved round a vertex. Each move shortens the path, so this only
    /// guards against a pathological surface; a path over a body settles in a handful.
    /// </summary>
    private const int MaxReroutes = 256;
}

/// <summary>
/// Which face lies across each edge of each face, and where each face sits - what a search over
/// the surface walks. Kept with the mesh, since the geometry cannot change and a path is asked
/// for on every frame of a drag.
/// </summary>
/// <remarks>
/// Vertices are joined by position rather than by index, so a mesh whose triangles each carry their
/// own copies of their corners still joins up. An edge three or more faces share is not walked
/// across: there is no single face on its far side.
/// </remarks>
internal sealed class FaceAdjacency
{
    private static readonly ConditionalWeakTable<object, FaceAdjacency> Kept = new();

    /// <summary>The face across edge k of face f - corner k to corner k + 1 - at f * 3 + k; -1 for none.</summary>
    private readonly int[] _across;

    private readonly Vec3[] _centroids;

    private FaceAdjacency(IMesh mesh)
    {
        Mesh = mesh;
        var vertices = mesh.Vertices;
        var triangles = mesh.Triangles;
        var faceCount = triangles.Length / 3;

        // One id per distinct position.
        Joined = new int[vertices.Length];
        var ids = new Dictionary<Vec3, int>(vertices.Length);
        for (var v = 0; v < vertices.Length; v++)
        {
            if (!ids.TryGetValue(vertices[v], out var id))
            {
                id = ids.Count;
                ids[vertices[v]] = id;
            }

            Joined[v] = id;
        }

        _across = new int[triangles.Length];
        Array.Fill(_across, -1);
        _centroids = new Vec3[faceCount];

        // The face-edges using each edge: the first two, and how many there were in all.
        var uses = new Dictionary<(int, int), (int First, int Second, int Count)>(triangles.Length);
        for (var f = 0; f < faceCount; f++)
        {
            _centroids[f] = (vertices[triangles[f * 3]] + vertices[triangles[(f * 3) + 1]] + vertices[triangles[(f * 3) + 2]]) / 3;

            for (var k = 0; k < 3; k++)
            {
                var key = EdgeKey(f, k, triangles);
                if (key.Item1 == key.Item2)
                {
                    continue;
                }

                var slot = (f * 3) + k;
                uses[key] = uses.TryGetValue(key, out var known)
                    ? (known.First, known.Count == 1 ? slot : known.Second, known.Count + 1)
                    : (slot, -1, 1);
            }
        }

        var stepTotal = 0.0;
        var steps = 0;
        foreach (var (first, second, count) in uses.Values)
        {
            if (count == 2)
            {
                _across[first] = second / 3;
                _across[second] = first / 3;
                stepTotal += _centroids[first / 3].DistanceTo(_centroids[second / 3]);
                steps++;
            }
        }

        _meanStep = steps > 0 ? stepTotal / steps : 0;
    }

    /// <summary>How far apart neighbouring faces sit, on average - the spacing the seed is sampled at.</summary>
    private readonly double _meanStep;

    public IMesh Mesh { get; }

    /// <summary>The id each vertex is joined under, indexed like <see cref="IMesh.Vertices"/>.</summary>
    public int[] Joined { get; }

    /// <summary>Whether face <paramref name="face"/> has the vertex joined as <paramref name="vertex"/> among its corners.</summary>
    public bool Touches(int face, int vertex)
    {
        var triangles = Mesh.Triangles;
        return Joined[triangles[face * 3]] == vertex
            || Joined[triangles[(face * 3) + 1]] == vertex
            || Joined[triangles[(face * 3) + 2]] == vertex;
    }

    /// <summary>
    /// <paramref name="corridor"/> with its run of faces around <paramref name="vertex"/> - the run
    /// portal <paramref name="portal"/> sits in - swapped for the faces on the vertex's other side,
    /// entering and leaving through the same two faces. The run may be where the corridor starts or
    /// ends - the path's own end can sit in a face round the vertex - and the arc then starts or ends
    /// there too. Null where there is no other side to go round: the vertex is on the surface's edge.
    ///
    /// <para>
    /// <paramref name="shorterWayOnly"/> refuses an other side with more faces than the run it would
    /// replace, give or take one. A vertex the path does not turn at is only worth going round the
    /// other way when that is the short way round it - as it is over a cube's corner - and trying
    /// every vertex the long way round is most of the cost of a path that is already right.
    /// </para>
    /// </summary>
    public List<int>? AroundTheOtherSide(List<int> corridor, int portal, int vertex, bool shorterWayOnly = false)
    {
        // Portal i lies between corridor faces i - 1 and i, and a turn at one of its ends has both.
        var first = portal - 1;
        var last = portal;
        if (first < 0 || last >= corridor.Count)
        {
            return null;
        }

        while (first > 0 && Touches(corridor[first - 1], vertex))
        {
            first--;
        }

        while (last + 1 < corridor.Count && Touches(corridor[last + 1], vertex))
        {
            last++;
        }

        // Round the vertex from the run's first face the way the run does not go, to its last.
        var arc = new List<int> { corridor[first] };
        var came = corridor[first + 1];
        var face = corridor[first];
        while (face != corridor[last])
        {
            var next = -1;
            for (var k = 0; k < 3; k++)
            {
                var across = _across[(face * 3) + k];
                if (across >= 0 && across != came && Touches(across, vertex) && SharesEdgeThrough(face, k, vertex))
                {
                    next = across;
                    break;
                }
            }

            if (next < 0 || next == corridor[first] || arc.Count > 64
                || (shorterWayOnly && arc.Count > last - first + 2))
            {
                return null;
            }

            (came, face) = (face, next);
            arc.Add(face);
        }

        var rerouted = new List<int>(corridor.Count + arc.Count);
        rerouted.AddRange(corridor.Take(first));
        rerouted.AddRange(arc);
        rerouted.AddRange(corridor.Skip(last + 1));
        return rerouted;
    }

    /// <summary>Whether edge <paramref name="k"/> of <paramref name="face"/> has <paramref name="vertex"/> at one end.</summary>
    private bool SharesEdgeThrough(int face, int k, int vertex)
    {
        var triangles = Mesh.Triangles;
        return Joined[triangles[(face * 3) + k]] == vertex || Joined[triangles[(face * 3) + ((k + 1) % 3)]] == vertex;
    }

    public static FaceAdjacency For(IMesh mesh) =>
        Kept.GetValue(mesh is ImmutableMesh immutable ? immutable.Measurements : mesh, _ => new FaceAdjacency(mesh));

    private (int, int) EdgeKey(int face, int k, ImmutableArray<int> triangles)
    {
        var a = Joined[triangles[(face * 3) + k]];
        var b = Joined[triangles[(face * 3) + ((k + 1) % 3)]];
        return a < b ? (a, b) : (b, a);
    }

    /// <summary>
    /// The band to start from: the straight line from <paramref name="from"/> to <paramref name="to"/>
    /// sampled about a face apart, each sample dropped onto the surface, and the faces they land on
    /// joined by searching between them. A face the band comes back to has the loop it closed cut
    /// out, since a band that visits a face twice cannot be laid flat. Null when nothing joins them.
    /// </summary>
    public List<int>? Seed(ISpatialIndex index, SurfacePoint from, SurfacePoint to)
    {
        var chord = to.Point - from.Point;
        var samples = _meanStep > 0 ? Math.Clamp((int)Math.Ceiling(chord.Length / _meanStep), 1, MaxSeedSamples) : 1;

        var waypoints = new List<int> { from.Triangle };
        for (var s = 1; s < samples; s++)
        {
            var landed = index.ClosestPoint(from.Point + (chord * ((double)s / samples)));
            if (landed.HasValue && landed.Value.Triangle != waypoints[^1])
            {
                waypoints.Add(landed.Value.Triangle);
            }
        }

        if (to.Triangle != waypoints[^1])
        {
            waypoints.Add(to.Triangle);
        }

        var band = new List<int> { waypoints[0] };
        var at = new Dictionary<int, int> { [waypoints[0]] = 0 };
        for (var w = 1; w < waypoints.Count; w++)
        {
            var leg = Corridor(band[^1], waypoints[w], _centroids[waypoints[w]]);
            if (leg is null)
            {
                return null;
            }

            foreach (var face in leg.Skip(1))
            {
                if (at.TryGetValue(face, out var earlier))
                {
                    foreach (var dropped in band.Skip(earlier + 1))
                    {
                        at.Remove(dropped);
                    }

                    band.RemoveRange(earlier + 1, band.Count - earlier - 1);
                    continue;
                }

                at[face] = band.Count;
                band.Add(face);
            }
        }

        return band;
    }

    /// <summary>Most samples taken along the seed line, whatever its length.</summary>
    private const int MaxSeedSamples = 4096;

    /// <summary>
    /// The faces from <paramref name="from"/> to <paramref name="to"/>, each sharing an edge with
    /// the next - an A* search, centroid to centroid, steered by the straight-line distance to
    /// <paramref name="target"/>. Null when nothing joins them.
    /// </summary>
    public List<int>? Corridor(int from, int to, Vec3 target)
    {
        var cost = new Dictionary<int, double> { [from] = 0 };
        var came = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, double>();
        open.Enqueue(from, _centroids[from].DistanceTo(target));

        while (open.TryDequeue(out var face, out _))
        {
            if (face == to)
            {
                var path = new List<int> { to };
                while (came.TryGetValue(path[^1], out var previous))
                {
                    path.Add(previous);
                }

                path.Reverse();
                return path;
            }

            if (!closed.Add(face))
            {
                continue;
            }

            for (var k = 0; k < 3; k++)
            {
                var next = _across[(face * 3) + k];
                if (next < 0 || closed.Contains(next))
                {
                    continue;
                }

                var g = cost[face] + _centroids[face].DistanceTo(_centroids[next]);
                if (cost.TryGetValue(next, out var known) && g >= known)
                {
                    continue;
                }

                cost[next] = g;
                came[next] = face;
                open.Enqueue(next, g + _centroids[next].DistanceTo(target));
            }
        }

        return null;
    }
}

/// <summary>The corridor laid flat, and the path pulled taut through it.</summary>
internal static class Funnel
{
    /// <summary>An edge the path crosses, flattened, with its ends named as a walker passing through it sees them.</summary>
    private readonly record struct Portal(Vec2 Left, Vec2 Right, Vec3 Left3, Vec3 Right3, int LeftVertex, int RightVertex);

    /// <summary>A place the taut path turns: an end of some portal, and the vertex there, or -1 at the path's own ends.</summary>
    private readonly record struct Corner(Vec2 At, Vec3 Point, int Portal, int Vertex);

    /// <summary>
    /// The path through one corridor: its points and length, and the vertices the corridor could be
    /// moved round - each with a portal it sits on, and whether the path turns there - the ones the
    /// path turns at first.
    /// </summary>
    public sealed record Pulled(ImmutableArray<Vec3> Points, double Length, List<(int Portal, int Vertex, bool Turns)> Pivots);

    public static Pulled Pull(FaceAdjacency faces, List<int> corridor, Vec3 from, Vec3 to)
    {
        var mesh = faces.Mesh;
        var vertices = mesh.Vertices;
        var triangles = mesh.Triangles;

        (int Id, Vec3 Point)[] CornersOf(int face) =>
        [
            (faces.Joined[triangles[face * 3]], vertices[triangles[face * 3]]),
            (faces.Joined[triangles[(face * 3) + 1]], vertices[triangles[(face * 3) + 1]]),
            (faces.Joined[triangles[(face * 3) + 2]], vertices[triangles[(face * 3) + 2]]),
        ];

        // The first face laid down as it is; each one after it unfolded across the edge it shares
        // with the one before. A vertex's flat position is overwritten whenever a face places it
        // again, which is right: the strip can come back round to a vertex it has already passed,
        // and only the placement beside the current face means anything there.
        var flat = new Dictionary<int, Vec2>();
        var first = CornersOf(corridor[0]);
        var ab = first[0].Point.DistanceTo(first[1].Point);
        flat[first[0].Id] = Vec2.Zero;
        flat[first[1].Id] = new Vec2(ab, 0);
        flat[first[2].Id] = Apex(Vec2.Zero, new Vec2(ab, 0),
            first[0].Point.DistanceTo(first[2].Point), first[1].Point.DistanceTo(first[2].Point), awayFrom: null);

        var start = Flatten(from, first, flat);
        var portals = new List<Portal> { new(start, start, from, from, -1, -1) };

        for (var i = 0; i + 1 < corridor.Count; i++)
        {
            var here = CornersOf(corridor[i]);
            var next = CornersOf(corridor[i + 1]);

            var u = here.First(c => next.Any(n => n.Id == c.Id));
            var v = here.Last(c => next.Any(n => n.Id == c.Id));
            var behind = here.First(c => c.Id != u.Id && c.Id != v.Id);
            var ahead = next.First(c => c.Id != u.Id && c.Id != v.Id);

            var (fu, fv, fb) = (flat[u.Id], flat[v.Id], flat[behind.Id]);
            flat[ahead.Id] = Apex(fu, fv, u.Point.DistanceTo(ahead.Point), v.Point.DistanceTo(ahead.Point), awayFrom: fb);

            // Walking from the face behind into the one ahead, which end is on the left.
            portals.Add(Cross(fu, fv, fb) > 0
                ? new Portal(fv, fu, v.Point, u.Point, v.Id, u.Id)
                : new Portal(fu, fv, u.Point, v.Point, u.Id, v.Id));
        }

        var end = Flatten(to, CornersOf(corridor[^1]), flat);
        portals.Add(new Portal(end, end, to, to, -1, -1));

        var corners = Taut(portals);
        var points = Crossings(portals, corners);

        var length = 0.0;
        for (var i = 1; i < points.Length; i++)
        {
            length += points[i - 1].DistanceTo(points[i]);
        }

        var pivots = new List<(int Portal, int Vertex, bool Turns)>();
        var seen = new HashSet<int>();
        foreach (var corner in corners.Where(c => c.Vertex >= 0))
        {
            if (seen.Add(corner.Vertex))
            {
                pivots.Add((corner.Portal, corner.Vertex, true));
            }
        }

        for (var i = 1; i + 1 < portals.Count; i++)
        {
            foreach (var vertex in new[] { portals[i].LeftVertex, portals[i].RightVertex })
            {
                if (seen.Add(vertex))
                {
                    pivots.Add((i, vertex, false));
                }
            }
        }

        return new Pulled(points, length, pivots);
    }

    /// <summary>
    /// Where the third corner of a triangle goes, given its distances from <paramref name="u"/>
    /// and <paramref name="v"/> - on the far side of u-v from <paramref name="awayFrom"/>, or on
    /// its left when there is nothing to keep away from.
    /// </summary>
    private static Vec2 Apex(Vec2 u, Vec2 v, double fromU, double fromV, Vec2? awayFrom)
    {
        var edge = v - u;
        var length = edge.Length;
        var along = ((fromU * fromU) - (fromV * fromV) + (length * length)) / (2 * length);
        var height = Math.Sqrt(Math.Max(0, (fromU * fromU) - (along * along)));
        var foot = u + (edge * (along / length));
        var normal = new Vec2(-edge.Y, edge.X) / length;

        var left = foot + (normal * height);
        if (awayFrom is not { } behind)
        {
            return left;
        }

        return Cross(u, v, behind) > 0 ? foot - (normal * height) : left;
    }

    /// <summary>A point on a face, at the same place on the face's flat copy.</summary>
    private static Vec2 Flatten(Vec3 point, (int Id, Vec3 Point)[] corners, Dictionary<int, Vec2> flat)
    {
        var (a, b, c) = (corners[0].Point, corners[1].Point, corners[2].Point);
        var (e0, e1, e2) = (b - a, c - a, point - a);
        double d00 = e0.Dot(e0), d01 = e0.Dot(e1), d11 = e1.Dot(e1), d20 = e2.Dot(e0), d21 = e2.Dot(e1);
        var denominator = (d00 * d11) - (d01 * d01);
        if (Math.Abs(denominator) < 1e-30)
        {
            return flat[corners[0].Id];
        }

        var wb = ((d11 * d20) - (d01 * d21)) / denominator;
        var wc = ((d00 * d21) - (d01 * d20)) / denominator;
        var wa = 1 - wb - wc;

        return (flat[corners[0].Id] * wa) + (flat[corners[1].Id] * wb) + (flat[corners[2].Id] * wc);
    }

    /// <summary>
    /// The funnel: the corners the taut path turns at, each one an end of some portal. Every
    /// portal narrows the funnel from one side or the other; a side that would cross over the
    /// other instead becomes a corner, and the funnel starts again from it.
    /// </summary>
    private static List<Corner> Taut(List<Portal> portals)
    {
        var corners = new List<Corner> { new(portals[0].Left, portals[0].Left3, 0, -1) };

        var apex = portals[0].Left;
        var (left, right) = (portals[0].Left, portals[0].Right);
        int apexIndex = 0, leftIndex = 0, rightIndex = 0;

        for (var i = 1; i < portals.Count; i++)
        {
            var portal = portals[i];

            // The right side moves in when the new right end is not outside the current one.
            if (Cross(apex, right, portal.Right) >= 0)
            {
                if (apex == right || Cross(apex, left, portal.Right) < 0)
                {
                    right = portal.Right;
                    rightIndex = i;
                }
                else
                {
                    // Past the left side: the path turns at the left end.
                    corners.Add(new Corner(left, portals[leftIndex].Left3, leftIndex, portals[leftIndex].LeftVertex));
                    apex = left;
                    apexIndex = leftIndex;
                    (left, right) = (apex, apex);
                    (leftIndex, rightIndex) = (apexIndex, apexIndex);
                    i = apexIndex;
                    continue;
                }
            }

            if (Cross(apex, left, portal.Left) <= 0)
            {
                if (apex == left || Cross(apex, right, portal.Left) > 0)
                {
                    left = portal.Left;
                    leftIndex = i;
                }
                else
                {
                    corners.Add(new Corner(right, portals[rightIndex].Right3, rightIndex, portals[rightIndex].RightVertex));
                    apex = right;
                    apexIndex = rightIndex;
                    (left, right) = (apex, apex);
                    (leftIndex, rightIndex) = (apexIndex, apexIndex);
                    i = apexIndex;
                    continue;
                }
            }
        }

        var last = portals.Count - 1;
        if (corners[^1].Portal != last)
        {
            corners.Add(new Corner(portals[last].Left, portals[last].Left3, last, -1));
        }

        return corners;
    }

    /// <summary>
    /// The path as points on the surface: its two ends, its corners, and every place it crosses an
    /// edge between them - each crossing taken back onto the surface by how far along its edge it
    /// lies, which unfolding leaves unchanged.
    /// </summary>
    private static ImmutableArray<Vec3> Crossings(List<Portal> portals, List<Corner> corners)
    {
        var path = ImmutableArray.CreateBuilder<Vec3>(portals.Count);
        path.Add(corners[0].Point);

        var segment = 0;
        for (var i = 1; i < portals.Count; i++)
        {
            while (segment + 1 < corners.Count - 1 && corners[segment + 1].Portal < i)
            {
                segment++;
            }

            Vec3 point;
            if (corners[segment + 1].Portal == i)
            {
                point = corners[segment + 1].Point;
            }
            else
            {
                var portal = portals[i];
                var (a, b) = (corners[segment].At, corners[segment + 1].At);
                var (along, across) = (b - a, portal.Right - portal.Left);
                var denominator = across.Cross(along);
                var t = Math.Abs(denominator) < 1e-30 ? 0.5 : Math.Clamp((portal.Left - a).Cross(along) / -denominator, 0, 1);
                point = portal.Left3 + ((portal.Right3 - portal.Left3) * t);
            }

            if (point.DistanceSquared(path[^1]) > 1e-24)
            {
                path.Add(point);
            }
        }

        return path.ToImmutable();
    }

    /// <summary>Twice the signed area of a-b-c: positive when c is left of a-b.</summary>
    private static double Cross(Vec2 a, Vec2 b, Vec2 c) => (b - a).Cross(c - a);
}
