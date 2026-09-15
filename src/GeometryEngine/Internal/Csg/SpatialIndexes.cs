namespace GeometryEngine.Internal.Csg;

/// <summary>A cell address in a uniform spatial hash.</summary>
internal readonly record struct Cell(long X, long Y, long Z);

/// <summary>
/// Collects vertices, merging any that fall within a tolerance of one already seen.
/// Cutting the same edge from two different triangles produces two points that agree
/// to the last few bits but are not bit-identical; without welding, the result mesh
/// would be a pile of loose triangles rather than a solid.
/// </summary>
internal sealed class VertexWelder(double tolerance)
{
    private readonly Dictionary<Cell, List<int>> _cells = [];
    private readonly List<Vec3> _vertices = [];
    private readonly double _tolerance = tolerance;
    private readonly double _toleranceSquared = tolerance * tolerance;

    public IReadOnlyList<Vec3> Vertices => _vertices;

    /// <summary>Returns the index of an existing near-identical vertex, or adds a new one.</summary>
    public int AddOrGet(Vec3 vertex)
    {
        var home = CellOf(vertex);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var neighbour = new Cell(home.X + dx, home.Y + dy, home.Z + dz);
                    if (!_cells.TryGetValue(neighbour, out var occupants))
                    {
                        continue;
                    }

                    foreach (var candidate in occupants)
                    {
                        if ((_vertices[candidate] - vertex).LengthSquared <= _toleranceSquared)
                        {
                            return candidate;
                        }
                    }
                }
            }
        }

        var index = _vertices.Count;
        _vertices.Add(vertex);

        if (!_cells.TryGetValue(home, out var bucket))
        {
            bucket = [];
            _cells[home] = bucket;
        }

        bucket.Add(index);
        return index;
    }

    private Cell CellOf(Vec3 vertex) => new(
        (long)Math.Floor(vertex.X / _tolerance),
        (long)Math.Floor(vertex.Y / _tolerance),
        (long)Math.Floor(vertex.Z / _tolerance));
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
