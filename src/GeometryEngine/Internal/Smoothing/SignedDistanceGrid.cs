using System.Collections.Immutable;
using GeometryEngine.Internal.Spatial;

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

    private SignedDistanceGrid(Vec3 origin, double cell, int nx, int ny, int nz, double[] values)
    {
        _origin = origin;
        _cell = cell;
        _nx = nx;
        _ny = ny;
        _nz = nz;
        _values = values;
    }

    public int CellCount => _values.Length;

    /// <summary>The grid spacing.</summary>
    public double Cell => _cell;

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

    /// <summary>A field that answers only near the surface: false for a point at least <c>within</c> away.</summary>
    public delegate bool BandField(Vec3 point, double within, out double value);

    /// <summary>
    /// <see cref="Sample"/> for a grid whose first use is <c>Shift(level)</c> and then
    /// <see cref="Reinitialise"/>, which is all a closing does with it - and which reads only two
    /// things: exact values on the grid edges the shifted level crosses, and the sign of every
    /// node. So the field is sampled exactly only within <paramref name="band"/> of the surface,
    /// where the bounded query is cheap to refuse everywhere else, and every node outside the band
    /// takes its sign from the band by flood fill, at a magnitude of the band itself.
    ///
    /// For a band wider than <paramref name="level"/> plus a cell, the result reinitialises to
    /// exactly the grid <see cref="Sample"/> would have given. A node outside the band is at least
    /// the band from the surface, so it sits on the same side of the shifted level as its stand-in
    /// value; its neighbours are within a cell of it, so no edge touching it can cross that level;
    /// and every edge that does cross lies between two exactly sampled nodes. The flood fill is
    /// right for the same reason: an out-of-band region cannot change sign without passing through
    /// the band. That holds for any field whose sign only changes at the surface - a mesh whose
    /// inside and outside are consistent. On one that is not, each filled region takes the sign
    /// most of the band around it agrees on, where full sampling would have kept whatever stray
    /// sign changes the defect causes out there.
    /// </summary>
    public static SignedDistanceGrid SampleBand(BandField field, Vec3 min, Vec3 max, double cell, double level, double band)
    {
        if (!(band > level + cell))
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "The band has to reach past the shifted level by more than a cell.");
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
                for (var i = 0; i < nx; i++)
                {
                    var point = new Vec3(min.X + (i * cell), min.Y + (j * cell), min.Z + (k * cell));
                    values[row + i] = field(point, band, out var value) ? value : double.NaN;
                }
            }
        });

        FillOutsideBand(values, nx, ny, nz, band);
        return new SignedDistanceGrid(min, cell, nx, ny, nz, values);
    }

    /// <summary>
    /// Gives every node the band left unsampled the sign of the band around it, at the band's
    /// magnitude. Each connected region of unsampled nodes is filled as one, with the sign most of
    /// the band nodes touching it agree on. On a consistent mesh they all agree. On one with a
    /// defect - a doubled face wound the wrong way flips the sign of the field near it - a few
    /// band nodes beside the defect disagree. Letting each unsampled node take the sign of the
    /// band node nearest it would hand those few everything closer to them than to the rest of
    /// the band: measured on such a bolus, a 14 mm blob.
    /// Regions are visited in index order and filled breadth first, so the result depends on
    /// nothing but the values.
    /// </summary>
    private static void FillOutsideBand(double[] values, int nx, int ny, int nz, double band)
    {
        var region = new int[values.Length];
        var queue = new int[values.Length];
        var plane = nx * ny;
        var tail = 0;
        var label = 0;

        for (var start = 0; start < values.Length; start++)
        {
            if (!double.IsNaN(values[start]) || region[start] != 0)
            {
                continue;
            }

            label++;
            var first = tail;
            var head = tail;
            region[start] = label;
            queue[tail++] = start;
            long inside = 0;
            long outside = 0;

            while (head < tail)
            {
                var index = queue[head++];
                var i = index % nx;
                var j = index / nx % ny;
                var k = index / plane;

                if (i > 0) { Reach(index - 1); }
                if (i + 1 < nx) { Reach(index + 1); }
                if (j > 0) { Reach(index - nx); }
                if (j + 1 < ny) { Reach(index + nx); }
                if (k > 0) { Reach(index - plane); }
                if (k + 1 < nz) { Reach(index + plane); }
            }

            // No band anywhere around it means no surface near the grid: outside, and the level
            // set comes back empty, as it would have from full sampling.
            var fill = inside > outside ? -band : band;
            for (var n = first; n < tail; n++)
            {
                values[queue[n]] = fill;
            }

            void Reach(int neighbour)
            {
                var value = values[neighbour];
                if (!double.IsNaN(value))
                {
                    if (value < 0) { inside++; } else { outside++; }
                }
                else if (region[neighbour] == 0)
                {
                    region[neighbour] = label;
                    queue[tail++] = neighbour;
                }
            }
        }
    }

    /// <summary>The node values in index order (x fastest, then y, then z), for comparing two grids.</summary>
    internal ReadOnlySpan<double> Values => _values;

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
    /// Recovers a true Euclidean signed distance field from the current zero level set, exactly
    /// within <paramref name="band"/> of it and as sign only beyond.
    ///
    /// This is the step that makes closing do anything, and it is worth being explicit about why.
    /// Shifting a true distance field by +d and then by -d returns exactly the field you started
    /// with, so an inflate/deflate cycle expressed as two shifts is the identity and smooths
    /// nothing. The rounding comes from the fact that once the surface has been grown, the
    /// distance to the *new* surface is not the old distance minus d anywhere the growing surface
    /// met itself across a concavity - and recovering that difference is exactly what
    /// re-computing the distance does. Without this call the operation is a no-op.
    ///
    /// The zero level is triangulated - each cell split into six tetrahedra along its diagonal,
    /// which neighbouring cells split the same way, so the surface is closed - and every node
    /// within the band gets its distance to those triangles, corrected for their sag. Measuring
    /// instead to the points where the level crosses grid edges, as an earlier version did,
    /// overestimates every distance, since the nearest crossing is rarely the nearest surface
    /// point: a closed sphere came out a tenth of a cell too large. Measuring to the flat
    /// triangles alone errs the other way, because a triangle cornered on a curved surface lies
    /// inside it: each reinitialisation then cut a little off in proportion to the curvature, and
    /// a round of the closing shrank a sphere by about 0.30 cells² × its mean curvature. So the
    /// distance is taken to the curved patch the triangle stands for instead - lifted at the
    /// nearest point by what its corners' normals say the surface bends there (<see cref="Sag"/>).
    /// That halves it, to about 0.15 cells² a round: close to the 0.175 cells² each round of the
    /// re-meshing route cuts for the same reason, and like it a step of curvature smoothing per
    /// iteration, scaling with the cell squared. Nodes beyond the band keep their sign at the
    /// band's magnitude, which is all the next shift reads from them as long as it moves the level
    /// less than the band less a cell.
    /// </summary>
    public void Reinitialise(double band)
    {
        var (vertices, normals, triangles) = TriangulateZeroLevel();
        if (triangles.IsEmpty)
        {
            // No surface anywhere on the grid: leave the signs and magnitudes as they are, and
            // the level set stays empty.
            return;
        }

        var surface = new MeshBvh(vertices, triangles);
        var plane = _nx * _ny;

        Parallel.For(0, _nz, k =>
        {
            for (var j = 0; j < _ny; j++)
            {
                for (var i = 0; i < _nx; i++)
                {
                    var index = (k * plane) + (j * _nx) + i;
                    var sign = _values[index] < 0 ? -1.0 : 1.0;

                    // The field was a distance before the shift that moved its level here, so a
                    // node's value already bounds how near the new level can be: one at least the
                    // band away from it needs no search to say so.
                    if (Math.Abs(_values[index]) >= band ||
                        !surface.TryClosestPoint(PositionOf(i, j, k), band, out var closest, out var triangle, out var distance))
                    {
                        _values[index] = sign * band;
                        continue;
                    }

                    // The surface bulges out of the triangle by the sag, so it is that much nearer
                    // a node outside and that much further from one inside. Each node reads only
                    // its own old value, for its sign, so the update can be made in place.
                    _values[index] = (sign * distance) - Sag(vertices, normals, triangle, closest);
                }
            }
        });
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

    private Vec3 PositionOf(int i, int j, int k) => new(
        _origin.X + (i * _cell),
        _origin.Y + (j * _cell),
        _origin.Z + (k * _cell));

    /// <summary>
    /// How far the surface a triangle approximates stands out of it at <paramref name="point"/>.
    /// Its corners lie on the surface, so between them the surface departs from the flat triangle
    /// by the quadratic each edge's normal change describes: for barycentric weights λ, half the
    /// sum over edges of λᵢλⱼ (nⱼ - nᵢ)·(pⱼ - pᵢ). On a sphere of radius R that is ℓ²/8R at the
    /// middle of an edge of length ℓ, its exact sag to second order. Clamped to a quarter of a cell,
    /// so a corner where the normals turn sharply cannot throw the distance about.
    /// </summary>
    private double Sag(ImmutableArray<Vec3> vertices, Vec3[] normals, int triangle, Vec3 point)
    {
        var ia = triangle * 3;
        Vec3 a = vertices[ia], b = vertices[ia + 1], c = vertices[ia + 2];
        Vec3 na = normals[ia], nb = normals[ia + 1], nc = normals[ia + 2];

        // Barycentric weights of the point, which lies on the triangle.
        var ab = b - a;
        var ac = c - a;
        var ap = point - a;
        var d00 = ab.Dot(ab);
        var d01 = ab.Dot(ac);
        var d11 = ac.Dot(ac);
        var denominator = (d00 * d11) - (d01 * d01);
        if (!(denominator > 1e-24))
        {
            return 0;
        }

        var d20 = ap.Dot(ab);
        var d21 = ap.Dot(ac);
        var wb = ((d11 * d20) - (d01 * d21)) / denominator;
        var wc = ((d00 * d21) - (d01 * d20)) / denominator;
        var wa = 1 - wb - wc;

        var sag = 0.5 * (
            (wa * wb * (nb - na).Dot(b - a)) +
            (wb * wc * (nc - nb).Dot(c - b)) +
            (wc * wa * (na - nc).Dot(a - c)));

        var limit = 0.25 * _cell;
        return Math.Clamp(sag, -limit, limit);
    }

    /// <summary>
    /// The zero level as a triangle soup, by marching tetrahedra: each cell is cut into the six
    /// tetrahedra that share its main diagonal, and each tetrahedron the level passes through
    /// contributes one or two triangles, cornered where it crosses the tetrahedron's edges. Each
    /// corner carries the surface normal there, from the field's gradient. Planes are triangulated
    /// in parallel and joined in order, so the soup depends only on the values.
    /// </summary>
    private (ImmutableArray<Vec3> Vertices, Vec3[] Normals, ImmutableArray<int> Triangles) TriangulateZeroLevel()
    {
        var slabs = new List<(Vec3 Point, Vec3 Normal)>[_nz - 1];

        Parallel.For(0, _nz - 1, k =>
        {
            var soup = new List<(Vec3, Vec3)>();
            Span<Vec3> corner = stackalloc Vec3[8];
            Span<Vec3> gradient = stackalloc Vec3[8];
            Span<double> value = stackalloc double[8];

            for (var j = 0; j < _ny - 1; j++)
            {
                for (var i = 0; i < _nx - 1; i++)
                {
                    var negative = 0;
                    for (var c = 0; c < 8; c++)
                    {
                        int ci = i + (c & 1), cj = j + ((c >> 1) & 1), ck = k + ((c >> 2) & 1);
                        value[c] = _values[IndexOf(ci, cj, ck)];
                        if (value[c] < 0) { negative++; }
                    }

                    if (negative == 0 || negative == 8)
                    {
                        continue;
                    }

                    for (var c = 0; c < 8; c++)
                    {
                        int ci = i + (c & 1), cj = j + ((c >> 1) & 1), ck = k + ((c >> 2) & 1);
                        corner[c] = PositionOf(ci, cj, ck);
                        gradient[c] = Gradient(ci, cj, ck);
                    }

                    // The six orders of stepping x, y and z from corner 0 to corner 7.
                    foreach (var (a, b) in DiagonalPaths)
                    {
                        March(soup, corner, gradient, value, 0, a, a | b, 7);
                    }
                }
            }

            slabs[k] = soup;
        });

        var count = slabs.Sum(slab => slab.Count);
        var vertices = ImmutableArray.CreateBuilder<Vec3>(count);
        var normals = new Vec3[count];
        foreach (var slab in slabs)
        {
            foreach (var (point, normal) in slab)
            {
                normals[vertices.Count] = normal;
                vertices.Add(point);
            }
        }

        var triangles = ImmutableArray.CreateBuilder<int>(count);
        for (var n = 0; n < count; n++)
        {
            triangles.Add(n);
        }

        return (vertices.MoveToImmutable(), normals, triangles.MoveToImmutable());
    }

    /// <summary>The field's gradient at a node: central differences, one-sided at the box.</summary>
    private Vec3 Gradient(int i, int j, int k)
    {
        double Along(int ia, int ja, int ka, int ib, int jb, int kb, double span) =>
            (_values[IndexOf(ib, jb, kb)] - _values[IndexOf(ia, ja, ka)]) / span;

        var gx = Along(Math.Max(i - 1, 0), j, k, Math.Min(i + 1, _nx - 1), j, k, (Math.Min(i + 1, _nx - 1) - Math.Max(i - 1, 0)) * _cell);
        var gy = Along(i, Math.Max(j - 1, 0), k, i, Math.Min(j + 1, _ny - 1), k, (Math.Min(j + 1, _ny - 1) - Math.Max(j - 1, 0)) * _cell);
        var gz = Along(i, j, Math.Max(k - 1, 0), i, j, Math.Min(k + 1, _nz - 1), (Math.Min(k + 1, _nz - 1) - Math.Max(k - 1, 0)) * _cell);
        return new Vec3(gx, gy, gz);
    }

    /// <summary>The first two axis steps of each path from corner 0 to corner 7, as corner bits.</summary>
    private static readonly (int, int)[] DiagonalPaths = [(1, 2), (1, 4), (2, 1), (2, 4), (4, 1), (4, 2)];

    /// <summary>Appends the piece of the zero level inside one tetrahedron of a cell.</summary>
    private static void March(
        List<(Vec3, Vec3)> soup, Span<Vec3> corner, Span<Vec3> gradient, Span<double> value, int p, int q, int r, int s)
    {
        Span<int> inside = stackalloc int[4];
        Span<int> outside = stackalloc int[4];
        var ni = 0;
        var no = 0;
        foreach (var c in (ReadOnlySpan<int>)[p, q, r, s])
        {
            if (value[c] < 0) { inside[ni++] = c; } else { outside[no++] = c; }
        }

        if (ni == 0 || no == 0)
        {
            return;
        }

        if (ni == 2)
        {
            var a = Crossing(corner, gradient, value, inside[0], outside[0]);
            var b = Crossing(corner, gradient, value, inside[0], outside[1]);
            var c = Crossing(corner, gradient, value, inside[1], outside[1]);
            var d = Crossing(corner, gradient, value, inside[1], outside[0]);
            soup.Add(a); soup.Add(b); soup.Add(c);
            soup.Add(a); soup.Add(c); soup.Add(d);
            return;
        }

        // One corner alone on its side: the level cuts the three edges that leave it.
        var lone = ni == 1 ? inside[0] : outside[0];
        var others = ni == 1 ? outside[..3] : inside[..3];
        soup.Add(Crossing(corner, gradient, value, lone, others[0]));
        soup.Add(Crossing(corner, gradient, value, lone, others[1]));
        soup.Add(Crossing(corner, gradient, value, lone, others[2]));
    }

    /// <summary>Where the level crosses an edge, and the unit normal there.</summary>
    private static (Vec3, Vec3) Crossing(Span<Vec3> corner, Span<Vec3> gradient, Span<double> value, int from, int to)
    {
        var t = value[from] / (value[from] - value[to]);
        var normal = gradient[from] + ((gradient[to] - gradient[from]) * t);
        var length = normal.Length;
        return (corner[from] + ((corner[to] - corner[from]) * t), length > 0 ? normal * (1 / length) : Vec3.Zero);
    }
}
