using GeometryEngine.Internal.Native;
using GeometryEngine.Internal.Spatial;

namespace GeometryEngine.Spatial;

/// <summary>Ask for a mesh to be prepared for spatial queries.</summary>
public sealed record BuildIndexRequest(IMesh Mesh);

internal sealed class BuildIndexHandler
{
    public Result<ISpatialIndex> Handle(BuildIndexRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        return request.Mesh.IsEmpty
            ? MeshErrors.EmptyOperand
            : Result.Success<ISpatialIndex>(new SpatialIndex(request.Mesh));
    }
}

/// <summary>
/// A mesh behind a managed BVH, with the native distance field built alongside on first batch
/// query where that library is present.
///
/// Single queries always go to the managed tree: one P/Invoke per query costs more than the
/// query. Batches go native, where the whole batch is one transition and runs in parallel.
/// </summary>
internal sealed class SpatialIndex : ISpatialIndex
{
    /// <summary>Below this a batch is not worth crossing to a native field even if one is built.</summary>
    private const int NativeBatchThreshold = 256;

    private readonly MeshBvh _bvh;
    private readonly int _triangleCount;
    private readonly object _nativeLock = new();
    private DistanceFieldHandle? _native;
    private bool _nativeTried;
    private volatile bool _disposed;

    public SpatialIndex(IMesh mesh)
    {
        Mesh = mesh;
        _bvh = new MeshBvh(mesh);
        _triangleCount = mesh.Triangles.Length / 3;
    }

    /// <summary>
    /// Whether a closest-point batch of this size should be answered natively.
    ///
    /// A field that is already built answers any batch worth the transition, so use it. One that
    /// is not has to be constructed first, and construction costs about what a managed pass over
    /// as many points as the mesh has triangles costs - so a single batch only repays it once it
    /// is roughly that large. Below that the managed parallel path finishes sooner and the field
    /// would have been built for one use.
    ///
    /// The size of the batch is therefore not enough on its own: an index kept across calls pays
    /// for a field once, on a batch big enough to deserve it, and every later batch rides on it,
    /// while an index built for a single call never pays for one at all. Decal work sits on the
    /// wrong side of that line - a few hundred points against a mesh of hundreds of thousands of
    /// triangles - and used to build a field per call and discard it.
    ///
    /// This is specific to closest points, where the managed path is cheap. Signed distance has
    /// no cheap managed path to fall back to; see the note there.
    /// </summary>
    private bool ShouldGoNative(int count)
    {
        if (count < NativeBatchThreshold || !DistanceFieldNative.IsAvailable)
        {
            return false;
        }

        lock (_nativeLock)
        {
            if (_nativeTried)
            {
                return _native is not null;
            }
        }

        return count >= _triangleCount;
    }

    public IMesh Mesh { get; }

    internal MeshBvh Tree => _bvh;

    public Maybe<RayHit> Raycast(Vec3 origin, Direction direction)
    {
        ThrowIfDisposed();

        if (!_bvh.Raycast(origin, direction.Vector, out var distance, out var triangle))
        {
            return Maybe<RayHit>.None();
        }

        return Maybe<RayHit>.Some(new RayHit(
            origin + (direction.Vector * distance),
            _bvh.TriangleNormal(triangle),
            distance,
            triangle));
    }

    public Maybe<SurfacePoint> ClosestPoint(Vec3 point)
    {
        ThrowIfDisposed();

        return _bvh.ClosestPoint(point, out var closest, out var triangle, out var distance)
            ? Maybe<SurfacePoint>.Some(new SurfacePoint(closest, _bvh.TriangleNormal(triangle), distance, triangle))
            : Maybe<SurfacePoint>.None();
    }

    public double SignedDistance(Vec3 point)
    {
        ThrowIfDisposed();
        return _bvh.SignedDistance(point);
    }

    public ImmutableArray<double> SignedDistances(ImmutableArray<Vec3> points)
    {
        ThrowIfDisposed();

        if (points.IsEmpty)
        {
            return ImmutableArray<double>.Empty;
        }

        // Size alone, unlike the closest-point batch below: the managed fallback here has to build
        // the pseudonormal tables before it can sign anything, and that costs about what the
        // native field costs. There is no cheap managed path to protect, so the old threshold
        // stands.
        var native = points.Length >= NativeBatchThreshold ? NativeField() : null;
        if (native is not null)
        {
            var results = new double[points.Length];
            var status = DistanceFieldNative.ge_field_signed_distance(
                native, NativeMeshArrays.Flatten(points), (nuint)points.Length, results);

            if (status == (int)DistanceFieldStatus.Ok)
            {
                return ImmutableArray.Create(results);
            }
        }

        var managed = new double[points.Length];
        Parallel.For(0, points.Length, i => managed[i] = _bvh.SignedDistance(points[i]));
        return ImmutableArray.Create(managed);
    }

    /// <summary>
    /// Closest points for a batch, for the slices that query hundreds of points against one
    /// surface. Natively in one call where possible.
    /// </summary>
    internal SurfacePoint[] ClosestPoints(IReadOnlyList<Vec3> points)
    {
        ThrowIfDisposed();

        var results = new SurfacePoint[points.Count];
        var native = ShouldGoNative(points.Count) ? NativeField() : null;

        if (native is not null)
        {
            var flat = NativeMeshArrays.Flatten([.. points]);
            var closest = new double[points.Count * 3];
            var triangles = new int[points.Count];
            var distances = new double[points.Count];

            if (DistanceFieldNative.ge_field_closest_point(native, flat, (nuint)points.Count, closest, triangles, distances)
                == (int)DistanceFieldStatus.Ok)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    results[i] = new SurfacePoint(
                        new Vec3(closest[i * 3], closest[(i * 3) + 1], closest[(i * 3) + 2]),
                        triangles[i] >= 0 ? _bvh.TriangleNormal(triangles[i]) : Vec3.Zero,
                        distances[i],
                        triangles[i]);
                }

                return results;
            }
        }

        Parallel.For(0, points.Count, i =>
        {
            results[i] = _bvh.ClosestPoint(points[i], out var point, out var triangle, out var distance)
                ? new SurfacePoint(point, _bvh.TriangleNormal(triangle), distance, triangle)
                : new SurfacePoint(points[i], Vec3.Zero, double.MaxValue, -1);
        });

        return results;
    }

    /// <summary>Native field for this mesh, built once; null when the library is absent or refuses the mesh.</summary>
    private DistanceFieldHandle? NativeField()
    {
        if (!DistanceFieldNative.IsAvailable)
        {
            return null;
        }

        lock (_nativeLock)
        {
            if (_nativeTried)
            {
                return _native;
            }

            _nativeTried = true;
            try
            {
                _native = DistanceFieldHandle.Create(Mesh);
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                _native = null;
            }

            return _native;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        _disposed = true;
        lock (_nativeLock)
        {
            _native?.Dispose();
            _native = null;
            _nativeTried = true;
        }
    }
}

/// <summary>
/// The index kept with a mesh, as handed out to anyone who asks for it. Every query passes
/// straight through; disposing it does nothing, because one holder disposing an index every other
/// holder is still using would be a fault waiting for the order of two calls to change. The
/// index's native memory is released when the mesh, and so the index, is collected.
/// </summary>
internal sealed class SharedSpatialIndex(SpatialIndex inner) : ISpatialIndex
{
    public SpatialIndex Inner { get; } = inner;

    public IMesh Mesh => Inner.Mesh;

    public Maybe<RayHit> Raycast(Vec3 origin, Direction direction) => Inner.Raycast(origin, direction);

    public Maybe<SurfacePoint> ClosestPoint(Vec3 point) => Inner.ClosestPoint(point);

    public double SignedDistance(Vec3 point) => Inner.SignedDistance(point);

    public ImmutableArray<double> SignedDistances(ImmutableArray<Vec3> points) => Inner.SignedDistances(points);

    public void Dispose()
    {
    }
}

/// <summary>Reaching the index kept with a mesh, for the slices that query a surface they were handed.</summary>
internal static class SharedIndexes
{
    /// <summary>
    /// The index kept with <paramref name="mesh"/>, built now if nothing has asked for it yet. A
    /// mesh that is not an <see cref="ImmutableMesh"/> has nowhere to keep one, and gets a fresh
    /// index each time.
    /// </summary>
    public static SharedSpatialIndex For(IMesh mesh) =>
        mesh is ImmutableMesh immutable
            ? (SharedSpatialIndex)immutable.Measurements.Index(() => new SharedSpatialIndex(new SpatialIndex(mesh)))
            : new SharedSpatialIndex(new SpatialIndex(mesh));

    /// <summary>The engine's own index behind one a caller supplied, whichever way they came by it.</summary>
    public static SpatialIndex? Unwrap(ISpatialIndex index) => index switch
    {
        SpatialIndex own => own,
        SharedSpatialIndex shared => shared.Inner,
        _ => null,
    };
}

/// <summary>The <see cref="ISpatialQueries"/> facade.</summary>
internal sealed class SpatialQueries : ISpatialQueries
{
    private readonly BuildIndexHandler _build = new();

    public Result<ISpatialIndex> BuildIndex(IMesh mesh) => _build.Handle(new BuildIndexRequest(mesh));

    public Result<ISpatialIndex> IndexFor(IMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        return mesh.IsEmpty
            ? MeshErrors.EmptyOperand
            : Result.Success<ISpatialIndex>(SharedIndexes.For(mesh));
    }
}
