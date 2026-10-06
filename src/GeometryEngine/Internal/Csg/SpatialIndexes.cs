using System.Runtime.InteropServices;

namespace GeometryEngine.Internal.Csg;

/// <summary>A cell address in a uniform spatial hash.</summary>
internal readonly record struct Cell(long X, long Y, long Z);

/// <summary>
/// Collects vertices, merging any that fall within a tolerance of one already seen.
/// Cutting the same edge from two different triangles produces two points that agree
/// to the last few bits but are not bit-identical; without welding, the result mesh
/// would be a pile of loose triangles rather than a solid.
/// </summary>
/// <remarks>
/// Kept vertices are filed in cells twice the tolerance across. Everything within the tolerance
/// of a vertex is then in its own cell or the one it is nearer to along each axis: eight cells
/// to look in, where cells the size of the tolerance meant twenty-seven, and looking is what a
/// weld spends its time on. A cell that size cannot hold many vertices that are all more than a
/// tolerance apart, so the chain in each stays short however the vertices crowd.
///
/// When several kept vertices are within reach, the one returned is the one a search of
/// tolerance-sized cells in order would have met first - see <see cref="Before"/>. Which is
/// returned decides the indices of the welded mesh, and this keeps them what they have always
/// been.
/// </remarks>
internal sealed class VertexWelder(double tolerance)
{
    /// <summary>
    /// How near the middle of a cell a coordinate may be before both neighbours are searched.
    /// Dividing by the cell size rounds, and a coordinate in the hundreds over a cell of two
    /// billionths rounds by a few millionths of a cell, so "which half" is not trusted to that
    /// precision.
    /// </summary>
    private const double Margin = 1e-3;

    /// <summary>Beyond this many cells from the origin a double no longer holds <see cref="Margin"/> of one.</summary>
    private const double FractionTrustedBelow = 1L << 40;

    /// <summary>The kept vertex most recently filed in each cell; the rest follow through <see cref="_next"/>.</summary>
    private readonly Dictionary<Cell, int> _cells = [];
    private readonly List<int> _next = [];
    private readonly List<Vec3> _vertices = [];
    private readonly double _tolerance = tolerance;
    private readonly double _cellSize = 2 * tolerance;
    private readonly double _toleranceSquared = tolerance * tolerance;

    public IReadOnlyList<Vec3> Vertices => _vertices;

    /// <summary>Returns the index of an existing near-identical vertex, or adds a new one.</summary>
    public int AddOrGet(Vec3 vertex)
    {
        var (x, fromX, toX) = Reach(vertex.X / _cellSize);
        var (y, fromY, toY) = Reach(vertex.Y / _cellSize);
        var (z, fromZ, toZ) = Reach(vertex.Z / _cellSize);

        var found = -1;
        for (var dx = fromX; dx <= toX; dx++)
        {
            for (var dy = fromY; dy <= toY; dy++)
            {
                for (var dz = fromZ; dz <= toZ; dz++)
                {
                    if (!_cells.TryGetValue(new Cell(x + dx, y + dy, z + dz), out var candidate))
                    {
                        continue;
                    }

                    for (; candidate >= 0; candidate = _next[candidate])
                    {
                        if ((_vertices[candidate] - vertex).LengthSquared <= _toleranceSquared
                            && (found < 0 || Before(candidate, found)))
                        {
                            found = candidate;
                        }
                    }
                }
            }
        }

        if (found >= 0)
        {
            return found;
        }

        var index = _vertices.Count;
        _vertices.Add(vertex);

        ref var newest = ref CollectionsMarshal.GetValueRefOrAddDefault(_cells, new Cell(x, y, z), out var occupied);
        _next.Add(occupied ? newest : -1);
        newest = index;

        return index;
    }

    /// <summary>
    /// The cell a coordinate falls in, and which neighbours along this axis hold anything within
    /// half a cell of it - the tolerance. Below the middle that is the cell before; above, the
    /// cell after; too close to the middle to say, or too far out to tell, both.
    /// </summary>
    private static (long Cell, int From, int To) Reach(double scaled)
    {
        var cell = Math.Floor(scaled);
        var within = scaled - cell;
        var unsure = Math.Abs(scaled) >= FractionTrustedBelow;

        return (
            (long)cell,
            unsure || within < 0.5 + Margin ? -1 : 0,
            unsure || within > 0.5 - Margin ? 1 : 0);
    }

    /// <summary>
    /// Whether one kept vertex would have been met before another by the search this replaced:
    /// cells the size of the tolerance, visited in order of x, then y, then z, and within a cell
    /// the vertex kept first. Only reached when two kept vertices are both near a third.
    /// </summary>
    private bool Before(int candidate, int other)
    {
        var (a, b) = (_vertices[candidate], _vertices[other]);

        var order = Math.Floor(a.X / _tolerance).CompareTo(Math.Floor(b.X / _tolerance));
        if (order == 0)
        {
            order = Math.Floor(a.Y / _tolerance).CompareTo(Math.Floor(b.Y / _tolerance));
        }

        if (order == 0)
        {
            order = Math.Floor(a.Z / _tolerance).CompareTo(Math.Floor(b.Z / _tolerance));
        }

        return order < 0 || (order == 0 && candidate < other);
    }
}

/// <summary>
/// A uniform grid over a fixed set of points, used to ask which vertices lie near
/// a given segment without scanning the whole mesh.
/// </summary>
internal sealed class PointGrid
{
    private readonly Dictionary<Cell, List<int>> _cells = [];
    private readonly IReadOnlyList<Vec3> _points;
    private readonly double _cellSize;

    public PointGrid(IReadOnlyList<Vec3> points, double cellSize)
    {
        _points = points;
        _cellSize = cellSize;

        for (var i = 0; i < points.Count; i++)
        {
            var cell = CellOf(points[i]);
            if (!_cells.TryGetValue(cell, out var bucket))
            {
                bucket = [];
                _cells[cell] = bucket;
            }

            bucket.Add(i);
        }
    }

    /// <summary>Adds every point within roughly one cell of the segment to <paramref name="into"/>.</summary>
    public void CollectNear(Vec3 from, Vec3 to, HashSet<int> into)
    {
        var length = (to - from).Length;
        var steps = (int)Math.Ceiling(length / _cellSize) + 1;

        for (var step = 0; step <= steps; step++)
        {
            var sample = from.LerpTo(to, (double)step / steps);
            var home = CellOf(sample);

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (_cells.TryGetValue(new Cell(home.X + dx, home.Y + dy, home.Z + dz), out var occupants))
                        {
                            foreach (var occupant in occupants)
                            {
                                into.Add(occupant);
                            }
                        }
                    }
                }
            }
        }
    }

    private Cell CellOf(Vec3 point) => new(
        (long)Math.Floor(point.X / _cellSize),
        (long)Math.Floor(point.Y / _cellSize),
        (long)Math.Floor(point.Z / _cellSize));
}
