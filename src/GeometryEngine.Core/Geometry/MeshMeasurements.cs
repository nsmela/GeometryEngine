using System.Collections.Immutable;

namespace GeometryEngine.Core.Geometry;

/// <summary>
/// What has been measured of one mesh's geometry, kept so the next caller asking reads it rather
/// than walking the mesh again. A mesh cannot change, so nothing here can go stale: an entry is
/// either absent or true of the geometry for as long as the geometry exists.
///
/// Shared by every <see cref="ImmutableMesh"/> carrying the same vertices and triangles - a
/// <see cref="ImmutableMesh.WithMetadata"/> copy reads and fills the same one - and seeded on a
/// mesh a rigid motion produced, from the mesh it moved (see <see cref="CarryRigid"/>).
/// </summary>
/// <remarks>
/// Safe to read and fill from several threads. Each entry is published as one reference, so a
/// reader sees an entry whole or not at all; two threads measuring at once both compute it and
/// the second write wins, which costs a measurement and never correctness, since both wrote the
/// same value.
/// </remarks>
internal sealed class MeshMeasurements
{
    private MeshStatistics? _statistics;
    private TopologyEntry? _topology;
    private NormalsEntry? _normals;
    private ISpatialIndex? _index;
    private object? _nativeSolid;
    private readonly object _indexGate = new();

    /// <summary>
    /// The spatial index over this geometry, built by <paramref name="build"/> the first time
    /// anything asks. Unlike the measurements above, this one is built under a lock: an index
    /// costs far more than waiting for another thread to finish building one. It is not carried
    /// through a transform, since it indexes positions, and every one of those has moved.
    /// </summary>
    public ISpatialIndex Index(Func<ISpatialIndex> build)
    {
        if (Volatile.Read(ref _index) is { } known)
        {
            return known;
        }

        lock (_indexGate)
        {
            if (_index is null)
            {
                Volatile.Write(ref _index, build());
            }

            return _index!;
        }
    }

    /// <summary>
    /// The solid a native kernel has kept for this geometry, if it has kept one. Opaque here:
    /// this assembly knows no kernel, only that one may leave something with a mesh. Like the
    /// index it is not carried through a transform, since it is the solid where it stood.
    /// </summary>
    public object? NativeSolid => Volatile.Read(ref _nativeSolid);

    /// <summary>
    /// Keeps <paramref name="candidate"/> unless something is kept already, and returns whichever
    /// is kept. Two threads reading the same mesh in at once both offer one; the first is kept and
    /// the second gets the first back, to release its own.
    /// </summary>
    public object KeepNativeSolid(object candidate) =>
        Interlocked.CompareExchange(ref _nativeSolid, candidate, null) ?? candidate;

    public MeshStatistics? Statistics
    {
        get => Volatile.Read(ref _statistics);
        set => Volatile.Write(ref _statistics, value);
    }

    public ImmutableArray<Vec3>? VertexNormals
    {
        get => Volatile.Read(ref _normals)?.Normals;
        set => Volatile.Write(ref _normals, value is { } normals ? new NormalsEntry(normals) : null);
    }

    /// <summary>
    /// The topology audit taken at <paramref name="tolerance"/>, if one has been. The audit counts
    /// slivers and coincident vertices against a tolerance, so an engine configured with another
    /// one has to take its own rather than read this.
    /// </summary>
    public TopologyValidation? TopologyAt(double tolerance) =>
        Volatile.Read(ref _topology) is { } entry && entry.Tolerance == tolerance ? entry.Topology : null;

    public void SetTopology(double tolerance, TopologyValidation topology) =>
        Volatile.Write(ref _topology, new TopologyEntry(tolerance, topology));

    /// <summary>The direction map of a motion that turns nothing - a translation.</summary>
    public static readonly Func<Vec3, Vec3> Unturned = direction => direction;

    /// <summary>
    /// Seeds the cache of a mesh that a rigid motion - a translation, a rotation, or both -
    /// produced from the one <paramref name="source"/> describes. <paramref name="turn"/> is how
    /// the motion moves a direction: its rotation alone, without the translation.
    ///
    /// A rigid motion keeps every distance and area, and leaves the triangles indexing the same
    /// vertices, so everything the topology audit counts reads the same afterwards: the edges and
    /// faces it pairs up, and the slivers and coincident vertices it measures against a tolerance.
    /// Volume, surface area and the counts carry over too. The bounds do not, and are passed in
    /// rather than read off the moved vertices, which the caller has just walked to build them.
    /// Normals turn with the mesh, so they are turned rather than recomputed from every face.
    /// </summary>
    public void CarryRigid(MeshMeasurements source, Func<Vec3, Vec3> turn, Vec3 boundsMin, Vec3 boundsMax)
    {
        if (source.Statistics is { } statistics)
        {
            Statistics = statistics with { BoundsMin = boundsMin, BoundsMax = boundsMax };
        }

        if (Volatile.Read(ref source._topology) is { } topology)
        {
            Volatile.Write(ref _topology, topology);
        }

        if (source.VertexNormals is { } normals)
        {
            if (ReferenceEquals(turn, Unturned))
            {
                VertexNormals = normals;
                return;
            }

            var turned = ImmutableArray.CreateBuilder<Vec3>(normals.Length);
            foreach (var normal in normals)
            {
                turned.Add(turn(normal));
            }

            VertexNormals = turned.MoveToImmutable();
        }
    }

    private sealed record TopologyEntry(double Tolerance, TopologyValidation Topology);

    // ImmutableArray is a struct, too wide to publish atomically; this boxes it in one reference.
    private sealed record NormalsEntry(ImmutableArray<Vec3> Normals);
}
