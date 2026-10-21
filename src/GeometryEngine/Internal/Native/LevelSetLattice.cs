using GeometryEngine.Core.Geometry.Primitives;

namespace GeometryEngine.Internal.Native;

/// <summary>
/// A field sampled up front, in parallel, at every point Manifold's level-set mesher will ask for,
/// so the sequential mesher - the only repeatable one - spends its time meshing rather than
/// sampling on one thread.
/// <para>
/// With no tolerance Manifold samples nothing but its lattice: a body-centred grid over the box,
/// <c>origin + spacing * (i + (w == 1 ? 0 : -0.5))</c> for i in [0, gridSize - w] on each axis,
/// with gridSize and spacing derived from the box and edge length as below (manifold
/// <c>src/sdf.cpp</c>, <c>CreateLevelSet</c> and <c>BoundedSDF</c>). That is mirrored here, and a
/// lookup only answers a point it recomputes bit-for-bit; the caller evaluates anything else
/// directly. So if Manifold ever samples differently the mesh gets slower, never different.
/// </para>
/// The native offset keeps the same lattice in <c>native/src/geometryengine_native.cpp</c>.
/// </summary>
internal sealed class LevelSetLattice
{
    /// <summary>Past this the grid is not one a cell budget would allow; it is sampled directly.</summary>
    private const long MaxSamplesPerLayer = 1L << 26;

    private readonly double[] _origin = new double[3];
    private readonly double[] _spacing = new double[3];
    private readonly int[] _size = new int[3];
    private readonly long _layer;
    private readonly double[]? _values;

    private LevelSetLattice(Vec3 min, Vec3 max, double edgeLength)
    {
        var lower = new[] { Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z) };
        var upper = new[] { Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z) };

        _layer = 1;
        for (var axis = 0; axis < 3; axis++)
        {
            _origin[axis] = lower[axis];
            var dim = upper[axis] - lower[axis];
            var cells = (dim / edgeLength) + 1.0;
            if (!(cells >= 2.0 && cells < 1.0e6))
            {
                return;
            }

            _size[axis] = (int)cells;
            _spacing[axis] = dim / (_size[axis] - 1);
            _layer *= _size[axis] + 1;
        }

        if (_layer <= MaxSamplesPerLayer)
        {
            _values = new double[2 * _layer];
        }
    }

    /// <summary>Samples <paramref name="field"/> at every lattice point, from several threads at once.</summary>
    public static LevelSetLattice Sample(Func<double, double, double, double> field, Vec3 min, Vec3 max, double edgeLength)
    {
        var lattice = new LevelSetLattice(min, max, edgeLength);
        if (lattice._values is { } values)
        {
            Parallel.For(0L, values.LongLength, index =>
            {
                lattice.Decode(index, out var w, out var i, out var j, out var k);
                if (lattice.InRange(w, i, j, k))
                {
                    values[index] = field(lattice.Coordinate(0, i, w), lattice.Coordinate(1, j, w), lattice.Coordinate(2, k, w));
                }
            });
        }

        return lattice;
    }

    public bool TryGet(double x, double y, double z, out double value)
    {
        value = 0;
        if (_values is null)
        {
            return false;
        }

        Span<double> point = [x, y, z];
        Span<int> lattice = stackalloc int[3];
        var w = -1;
        for (var axis = 0; axis < 3; axis++)
        {
            // Twice the position in cells is even on the corner lattice and odd on the centres.
            var twice = 2.0 * (point[axis] - _origin[axis]) / _spacing[axis];
            if (!(Math.Abs(twice) < 4.0e6))
            {
                return false;
            }

            var half = (long)Math.Round(twice);
            var axisW = (half & 1) == 0 ? 1 : 0;
            if (w >= 0 && axisW != w)
            {
                return false;
            }

            w = axisW;
            lattice[axis] = (int)(w == 1 ? half / 2 : (half + 1) / 2);
        }

        if (!InRange(w, lattice[0], lattice[1], lattice[2])
            || Coordinate(0, lattice[0], w) != x
            || Coordinate(1, lattice[1], w) != y
            || Coordinate(2, lattice[2], w) != z)
        {
            return false;
        }

        value = _values[Encode(w, lattice[0], lattice[1], lattice[2])];
        return true;
    }

    private double Coordinate(int axis, int index, int w) =>
        _origin[axis] + (_spacing[axis] * (index + (w == 1 ? 0.0 : -0.5)));

    private bool InRange(int w, int i, int j, int k) =>
        i >= 0 && j >= 0 && k >= 0 && i <= _size[0] - w && j <= _size[1] - w && k <= _size[2] - w;

    private long Encode(int w, int i, int j, int k) =>
        (w * _layer) + (((((long)i * (_size[1] + 1)) + j) * (_size[2] + 1)) + k);

    private void Decode(long index, out int w, out int i, out int j, out int k)
    {
        w = (int)(index / _layer);
        index %= _layer;
        k = (int)(index % (_size[2] + 1));
        index /= _size[2] + 1;
        j = (int)(index % (_size[1] + 1));
        i = (int)(index / (_size[1] + 1));
    }
}
