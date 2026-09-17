using System.Collections.Immutable;

namespace GeometryEngine.Internal.Smoothing;

/// <summary>
/// Rounds creases and leaves everything else exactly where it was, by doing two things that
/// plain fairing does not: it only moves vertices near a fold, and it refuses to let any vertex
/// stray further than a named distance from the original surface.
///
/// The gate is what keeps flat and gently curved surface accurate - those vertices are never
/// written, so they come back bit-identical rather than nearly so. The cap is what makes the
/// accuracy a guarantee instead of a measurement: after every pass each moved vertex is pulled
/// back inside a sphere of the given radius about where it started, so "no point moved more than
/// this far" holds by construction whatever the iteration count.
///
/// The cap is deliberately on displacement from the vertex's own starting position rather than on
/// its distance to the original surface, which was how it was written first and which does not
/// work. Distance to the surface leaves a vertex free to slide *along* it, and sliding is
/// unbounded: a cube smoothed under a 0.5 mm surface band lost 85 % of its volume, because every
/// corner had slid to the middle of a face while remaining, quite legally, half a millimetre from
/// the original surface. Bounding the displacement forbids that and implies the surface bound as
/// well, since the starting position is itself on the surface.
///
/// Both matter because the alternatives fail differently. Ungated fairing touches the whole
/// surface, and on a mesh with thin walls or sharp rims it collapses them. Manifold's tangent
/// interpolation preserves flat surface exactly but, where it does act on a crease, bulges the
/// neighbourhood outwards by whatever the vertex spacing allows. Neither offers a bound.
/// </summary>
internal static class CreaseSmoother
{
    /// <summary>Taubin's pass-band frequency; see <see cref="LaplacianSmoother"/>.</summary>
    private const double PassBand = 0.1;

    /// <summary>
    /// How far past the crease the movable set is grown. One ring puts the whole correction into
    /// the triangles touching the fold, which reads as a kink; two gives the rounding room to
    /// blend into the surface either side without reaching far enough to drag unrelated detail.
    /// </summary>
    private const int BlendRings = 2;

    public static Smoothed Smooth(
        IMesh mesh, double roundSharperThan, double maxDeviation, int iterations, double strength)
    {
        var positions = mesh.Vertices.ToArray();
        var triangles = mesh.Triangles;

        var (start, neighbours) = MeshAdjacency.Build(positions.Length, triangles);
        var boundary = MeshAdjacency.FindBoundaryVertices(positions.Length, triangles);
        var crease = MeshAdjacency.FindCreaseVertices(mesh, roundSharperThan, out var creaseEdges);
        var movable = MeshAdjacency.Dilate(crease, start, neighbours, BlendRings);

        // A rim is held still even when it is also a crease: it has no ring of neighbours to be
        // averaged against, so smoothing it only shrinks the hole.
        var moved = 0;
        for (var v = 0; v < movable.Length; v++)
        {
            if (boundary[v])
            {
                movable[v] = false;
            }

            if (movable[v])
            {
                moved++;
            }
        }

        if (moved == 0 || iterations == 0)
        {
            return new Smoothed([.. positions], triangles, creaseEdges, 0);
        }

        // Measured against the positions as they arrived, not against the previous pass, so the
        // bound cannot drift outwards one pass at a time.
        var anchors = mesh.Vertices.ToArray();

        var mu = 1.0 / (PassBand - (1.0 / strength));
        var buffer = new Vec3[positions.Length];

        for (var i = 0; i < iterations; i++)
        {
            Pass(positions, buffer, start, neighbours, movable, strength, anchors, maxDeviation);
            Pass(positions, buffer, start, neighbours, movable, mu, anchors, maxDeviation);
        }

        return new Smoothed([.. positions], triangles, creaseEdges, moved);
    }

    /// <summary>The result, with what it decided to act on - the caller reports it in metadata.</summary>
    public readonly record struct Smoothed(
        ImmutableArray<Vec3> Vertices,
        ImmutableArray<int> Triangles,
        int CreaseEdges,
        int MovedVertices);

    private static void Pass(
        Vec3[] positions,
        Vec3[] buffer,
        int[] start,
        int[] neighbours,
        bool[] movable,
        double factor,
        Vec3[] anchors,
        double maxDeviation)
    {
        for (var v = 0; v < positions.Length; v++)
        {
            var from = start[v];
            var to = start[v + 1];

            if (!movable[v] || to == from)
            {
                buffer[v] = positions[v];
                continue;
            }

            var sum = Vec3.Zero;
            for (var n = from; n < to; n++)
            {
                sum += positions[neighbours[n]];
            }

            var centroid = sum / (to - from);
            buffer[v] = Clamped(positions[v] + ((centroid - positions[v]) * factor), anchors[v], maxDeviation);
        }

        Array.Copy(buffer, positions, positions.Length);
    }

    /// <summary>
    /// Holds a vertex within <paramref name="maxDeviation"/> of where it started. Past the limit
    /// it is put back on that sphere along the line it travelled, which is the shortest move
    /// satisfying the bound and so disturbs the smoothing least.
    /// </summary>
    private static Vec3 Clamped(Vec3 candidate, Vec3 anchor, double maxDeviation)
    {
        var travel = candidate - anchor;
        var distance = travel.Length;

        return distance <= maxDeviation ? candidate : anchor + (travel / distance * maxDeviation);
    }
}
