namespace GeometryEngine.Internal.Smoothing;

/// <summary>
/// A signed distance field sampled onto a regular grid, with the two operations morphological
/// closing needs: shifting the level, and recovering a true distance field afterwards.
///
/// The point of holding the field on a grid is that an inflate/deflate cycle can be iterated
/// without ever going back to a mesh. Re-meshing between steps costs a level-set meshing and a
/// fresh field build each time, and worse, each pass resamples a surface that was itself
/// resampled - so grid-resolution error compounds with every iteration rather than being paid
/// once.
/// </summary>
internal sealed class SignedDistanceGrid
{
    private readonly Vec3 _origin;
    private readonly double _cell;
    private readonly int _nx;
    private readonly int _ny;
    private readonly int _nz;
    private readonly double[] _values;

    /// <summary>
    /// Nearest point on the zero level set, per node, reused across reinitialisations rather
    /// than reallocated - this is the largest allocation in the operation.
    /// </summary>
    private readonly Vec3[] _closest;
    private readonly bool[] _known;

    private SignedDistanceGrid(Vec3 origin, double cell, int nx, int ny, int nz, double[] values)
    {
        _origin = origin;
        _cell = cell;
        _nx = nx;
        _ny = ny;
        _nz = nz;
        _values = values;
        _closest = new Vec3[values.Length];
        _known = new bool[values.Length];
    }

    public int CellCount => _values.Length;

    /// <summary>The field at every node, x fastest, then y, then z. For tests and measurement.</summary>
    internal ReadOnlySpan<double> Values => _values;

    /// <summary>
    /// Samples <paramref name="field"/> over the box, one point per grid node. This is the only
    /// time the mesh is consulted; everything after it happens on the grid.
    /// </summary>
    public static SignedDistanceGrid Sample(Func<Vec3, double> field, Vec3 min, Vec3 max, double cell)
    {
        var nx = Math.Max(2, (int)Math.Ceiling((max.X - min.X) / cell) + 1);
        var ny = Math.Max(2, (int)Math.Ceiling((max.Y - min.Y) / cell) + 1);
        var nz = Math.Max(2, (int)Math.Ceiling((max.Z - min.Z) / cell) + 1);

        var values = new double[nx * ny * nz];

        // One plane per task: enough work each to cover the scheduling, and different planes
        // never write the same cache line except at their shared boundary.
        Parallel.For(0, nz, k =>
        {
            for (var j = 0; j < ny; j++)
            {
                var row = ((k * ny) + j) * nx;
                for (var i = 0; i < nx; i++)
                {
                    values[row + i] = field(new Vec3(
                        min.X + (i * cell),
                        min.Y + (j * cell),
                        min.Z + (k * cell)));
                }
            }
        });

        return new SignedDistanceGrid(min, cell, nx, ny, nz, values);
    }

    /// <summary>
    /// Samples a surface over the box exactly where it is nearer than <paramref name="reach"/>,
    /// and elsewhere records only which side of it the node is on, as plus or minus the reach.
    ///
    /// For a caller that reads exact distances only near the surface - a closing reads them within
    /// its inflation and a cell or two - this is the grid <see cref="Sample"/> would give as far as
    /// that caller can tell, for a fraction of the searching: most nodes of a box are not near the
    /// surface, and a node that is not is answered as soon as the search finds nothing in reach.
    ///
    /// A node out of reach takes the side of the node before it in its row. That is sound because
    /// distance changes by at most a cell from one node to the next, so with a reach wider than a
    /// cell no step can pass from out of reach on one side to out of reach on the other: between
    /// two such nodes there is always one within reach, which is measured and says which side it
    /// is on. Only the first out-of-reach node of a row has nothing before it, and it is asked
    /// outright.
    ///
    /// The surface must separate inside from outside for a side to mean anything. A mesh with a
    /// hole in it, or a face wound backwards, should be sampled with <see cref="Sample"/>.
    /// </summary>
    public static SignedDistanceGrid SampleNear(Spatial.MeshBvh surface, Vec3 min, Vec3 max, double cell, double reach)
    {
        if (!(reach > cell))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reach), reach, $"The reach must be wider than the cell ({cell}) for a side to carry from one node to the next.");
        }

        var nx = Math.Max(2, (int)Math.Ceiling((max.X - min.X) / cell) + 1);
        var ny = Math.Max(2, (int)Math.Ceiling((max.Y - min.Y) / cell) + 1);
        var nz = Math.Max(2, (int)Math.Ceiling((max.Z - min.Z) / cell) + 1);

        var values = new double[nx * ny * nz];

        Parallel.For(0, nz, k =>
        {
            for (var j = 0; j < ny; j++)
            {
                var row = ((k * ny) + j) * nx;

                // Which side the last node was on: zero until this row has one to go by.
                var side = 0.0;
                for (var i = 0; i < nx; i++)
                {
                    var point = new Vec3(min.X + (i * cell), min.Y + (j * cell), min.Z + (k * cell));
                    if (surface.TrySignedDistance(point, reach, out var distance))
                    {
                        side = distance < 0 ? -1 : 1;
                        values[row + i] = distance;
                        continue;
                    }

                    if (side == 0)
                    {
                        side = surface.SignedDistance(point) < 0 ? -1 : 1;
                    }

                    values[row + i] = side * reach;
                }
            }
        });

        return new SignedDistanceGrid(min, cell, nx, ny, nz, values);
    }

    /// <summary>
    /// Moves the zero level by <paramref name="distance"/>: positive grows the solid, negative
    /// shrinks it. Exact for a true signed distance field, which is why an offset is a shift here
    /// rather than a re-computation.
    /// </summary>
    public void Shift(double distance)
    {
        for (var i = 0; i < _values.Length; i++)
        {
            _values[i] -= distance;
        }
    }

    /// <summary>
    /// Recovers a true Euclidean signed distance field from the current zero level set.
    ///
    /// This is the step that makes closing do anything, and it is worth being explicit about why.
    /// Shifting a true distance field by +d and then by -d returns exactly the field you started
    /// with, so an inflate/deflate cycle expressed as two shifts is the identity and smooths
    /// nothing. The rounding comes from the fact that once the surface has been grown, the
    /// distance to the *new* surface is not the old distance minus d anywhere the growing surface
    /// met itself across a concavity - and recovering that difference is exactly what
    /// re-computing the distance does. Without this call the operation is a no-op.
    ///
    /// Distances propagate as closest *points* rather than as scalars. A scalar transform has to
    /// combine a node's sub-cell offset to the surface with its whole-cell distance to the seed
    /// in quadrature, which underestimates the true distance by up to half a cell. That error is
    /// one-sided, so the field comes out slightly too small everywhere, every shift then travels
    /// slightly too far, and a closed sphere loses radius on each round - about 0.3 cells per
    /// reinitialisation when measured. Carrying the closest point makes the distance a true
    /// Euclidean norm to a real surface position, which removes the bias rather than shrinking it.
    /// </summary>
    public void Reinitialise()
    {
        SeedFromCrossings();

        // Two round trips. A single forward and backward sweep propagates a closest point along
        // any monotone path, which covers most of the grid; the second catches the nodes whose
        // nearest seed lies around a corner from them.
        Sweep(forward: true);
        Sweep(forward: false);
        Sweep(forward: true);
        Sweep(forward: false);

        for (var index = 0; index < _values.Length; index++)
        {
            if (!_known[index])
            {
                // Nothing reached it, which means there is no surface anywhere on the grid. Leave
                // the sign and a magnitude larger than the box so the level set stays empty.
                continue;
            }

            var distance = (PositionOf(index) - _closest[index]).Length;
            _values[index] = _values[index] < 0 ? -distance : distance;
        }
    }

    /// <summary>
    /// The field at an arbitrary point, trilinearly interpolated and clamped at the box. Handed
    /// to the level-set mesher as the field to mesh once the iterations are done.
    /// </summary>
    public double Sample(Vec3 point)
    {
        var gx = Math.Clamp((point.X - _origin.X) / _cell, 0, _nx - 1);
        var gy = Math.Clamp((point.Y - _origin.Y) / _cell, 0, _ny - 1);
        var gz = Math.Clamp((point.Z - _origin.Z) / _cell, 0, _nz - 1);

        var i0 = Math.Min((int)gx, _nx - 2);
        var j0 = Math.Min((int)gy, _ny - 2);
        var k0 = Math.Min((int)gz, _nz - 2);

        var tx = gx - i0;
        var ty = gy - j0;
        var tz = gz - k0;

        var x00 = Lerp(At(i0, j0, k0), At(i0 + 1, j0, k0), tx);
        var x10 = Lerp(At(i0, j0 + 1, k0), At(i0 + 1, j0 + 1, k0), tx);
        var x01 = Lerp(At(i0, j0, k0 + 1), At(i0 + 1, j0, k0 + 1), tx);
        var x11 = Lerp(At(i0, j0 + 1, k0 + 1), At(i0 + 1, j0 + 1, k0 + 1), tx);

        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private double At(int i, int j, int k) => _values[(((k * _ny) + j) * _nx) + i];

    private int IndexOf(int i, int j, int k) => (((k * _ny) + j) * _nx) + i;

    private Vec3 PositionOf(int index)
    {
        var i = index % _nx;
        var j = index / _nx % _ny;
        var k = index / (_nx * _ny);

        return PositionOf(i, j, k);
    }

    private Vec3 PositionOf(int i, int j, int k) => new(
        _origin.X + (i * _cell),
        _origin.Y + (j * _cell),
        _origin.Z + (k * _cell));

    /// <summary>
    /// Finds the zero crossing on each grid edge that has one, and gives both its nodes that
    /// crossing point as their closest known surface position. Everything else starts unknown and
    /// is filled in by the sweeps.
    /// </summary>
    private void SeedFromCrossings()
    {
        Array.Clear(_known);

        for (var k = 0; k < _nz; k++)
        {
            for (var j = 0; j < _ny; j++)
            {
                for (var i = 0; i < _nx; i++)
                {
                    var index = IndexOf(i, j, k);
                    var value = _values[index];

                    var here = PositionOf(i, j, k);
                    if (i + 1 < _nx) { SeedEdge(index, here, i + 1, j, k, value); }
                    if (j + 1 < _ny) { SeedEdge(index, here, i, j + 1, k, value); }
                    if (k + 1 < _nz) { SeedEdge(index, here, i, j, k + 1, value); }
                }
            }
        }
    }

    /// <summary>
    /// If the field changes sign along an edge, interpolates where it crosses and offers that
    /// point to both ends. Sub-cell by construction: the crossing is a real position, not the
    /// nearer node.
    /// </summary>
    private void SeedEdge(int here, Vec3 from, int ti, int tj, int tk, double valueHere)
    {
        var there = IndexOf(ti, tj, tk);
        var valueThere = _values[there];
        if ((valueHere < 0) == (valueThere < 0))
        {
            return;
        }

        var span = Math.Abs(valueHere) + Math.Abs(valueThere);
        var t = span > 0 ? Math.Abs(valueHere) / span : 0.5;

        var to = PositionOf(ti, tj, tk);
        var crossing = from + ((to - from) * t);

        Offer(here, from, crossing);
        Offer(there, to, crossing);
    }

    /// <summary>
    /// Keeps <paramref name="candidate"/> for a node if it is nearer than what it already has. The
    /// node's position is passed in by callers that already know its coordinates: recovering them
    /// from the flat index costs three integer divisions, on every offer of every sweep.
    /// </summary>
    private void Offer(int index, Vec3 position, Vec3 candidate)
    {
        if (!_known[index])
        {
            _closest[index] = candidate;
            _known[index] = true;
            return;
        }

        if ((candidate - position).LengthSquared < (_closest[index] - position).LengthSquared)
        {
            _closest[index] = candidate;
        }
    }

    /// <summary>
    /// One raster sweep, propagating each node's closest surface point to the three neighbours
    /// ahead of it. Forward covers increasing indices, backward decreasing, so between them every
    /// axis-monotone path is walked.
    /// </summary>
    private void Sweep(bool forward)
    {
        var step = forward ? 1 : -1;
        var kStart = forward ? 0 : _nz - 1;
        var jStart = forward ? 0 : _ny - 1;
        var iStart = forward ? 0 : _nx - 1;

        for (var k = kStart; k >= 0 && k < _nz; k += step)
        {
            for (var j = jStart; j >= 0 && j < _ny; j += step)
            {
                for (var i = iStart; i >= 0 && i < _nx; i += step)
                {
                    var index = IndexOf(i, j, k);
                    if (!_known[index])
                    {
                        continue;
                    }

                    var closest = _closest[index];

                    var ni = i + step;
                    if (ni >= 0 && ni < _nx) { Offer(IndexOf(ni, j, k), PositionOf(ni, j, k), closest); }

                    var nj = j + step;
                    if (nj >= 0 && nj < _ny) { Offer(IndexOf(i, nj, k), PositionOf(i, nj, k), closest); }

                    var nk = k + step;
                    if (nk >= 0 && nk < _nz) { Offer(IndexOf(i, j, nk), PositionOf(i, j, nk), closest); }
                }
            }
        }
    }
}
