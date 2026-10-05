using GeometryEngine.Booleans;
using GeometryEngine.Decals;
using GeometryEngine.Evaluators;
using GeometryEngine.Generators;
using GeometryEngine.Internal;
using GeometryEngine.MeshIO;
using GeometryEngine.Modifiers;
using GeometryEngine.Polygons;
using GeometryEngine.Spatial;
using GeometryEngine.Transforms;

namespace GeometryEngine;

/// <summary>
/// A geometry engine backed by binary space partitioning, in pure managed code.
///
/// The engine holds no mutable state, so a single instance is safe to share across
/// threads and across operations. It exists only to compose the slices; every
/// decision about geometry lives in the slice that owns it.
/// </summary>
public sealed class BspGeometryEngine : IGeometryEngine
{
    public IBooleans Booleans { get; }
    public IGeometryGenerators Generators { get; }
    public IGeometryEvaluators Evaluators { get; }
    public IGeometryTransforms Transforms { get; }
    public IGeometryIO IO { get; }
    public IGeometryModifiers Modifiers { get; }
    public ISpatialQueries Spatial { get; }
    public IPolygonOperations Polygons { get; }
    public IDecalOperations Decals { get; }

    private BspGeometryEngine(IBooleans booleans, Tolerance meshTolerance)
    {
        Booleans = booleans;
        Generators = new GeometryGenerators();
        Evaluators = new GeometryEvaluators(meshTolerance);
        Transforms = new GeometryTransforms();
        IO = new GeometryIO(meshTolerance);
        Modifiers = new GeometryModifiers();
        Spatial = new SpatialQueries();
        Polygons = new PolygonOperations();
        Decals = new DecalOperations();
    }

    private BspGeometryEngine(IToleranceStrategy booleanTolerance, Tolerance meshTolerance)
        : this(new BooleanOperations(booleanTolerance), meshTolerance)
    {
    }

    /// <summary>
    /// The default engine: Boolean operations are backed by the native Manifold kernel for
    /// guaranteed-watertight, high-performance CSG, falling back to the managed BSP kernel
    /// where the native one declines or is unavailable.
    ///
    /// The choice is deliberately not configurable by environment variable. The two kernels
    /// do not offer the same guarantee - only the native one promises watertight output - so
    /// letting ambient process state decide which runs would make a caller's results depend
    /// on something it cannot see. Ask for a kernel by name instead:
    /// <see cref="CreateWithManifold"/> or <see cref="CreateManagedBsp()"/>. Every result
    /// records the kernel that produced it in <see cref="MeshMetadata.CreatedBy"/>.
    /// </summary>
    public static IGeometryEngine Create() => CreateWithManifold();

    /// <summary>
    /// An engine backed by the native Manifold kernel, with automatic fallback to the
    /// managed BSP engine when an input mesh is not a valid 2-manifold (e.g. open surface
    /// with boundaries), or when the native library is not available for this platform.
    /// </summary>
    public static IGeometryEngine CreateWithManifold() => CreateWithManifold(SolidRetention.Keep);

    /// <summary>
    /// <see cref="CreateWithManifold()"/>, saying whether the native kernel keeps the solids it
    /// reads in and builds. Keeping them is the default and roughly halves the cost of using a
    /// mesh again; <see cref="SolidRetention.None"/> trades that for the native memory they hold.
    /// </summary>
    public static IGeometryEngine CreateWithManifold(SolidRetention retention) =>
        new BspGeometryEngine(
            new ManifoldBooleanOperations(
                new BooleanOperations(new AdaptiveTolerance(AdaptiveTolerance.DefaultFactor)), retention),
            Tolerance.Welding);

    /// <summary>
    /// An engine backed by the pure managed BSP kernel with adaptive tolerance.
    /// </summary>
    public static IGeometryEngine CreateManagedBsp() =>
        new BspGeometryEngine(new AdaptiveTolerance(AdaptiveTolerance.DefaultFactor), Tolerance.Welding);

    /// <summary>
    /// An engine that runs every operation at one caller-supplied tolerance with the managed BSP kernel.
    /// </summary>
    public static IGeometryEngine CreateManagedBsp(Tolerance tolerance) =>
        new BspGeometryEngine(new FixedTolerance(tolerance), tolerance);

    /// <summary>
    /// An engine that runs every operation at one caller-supplied tolerance, for when a
    /// model needs a specific value rather than the size-relative default.
    /// </summary>
    public static IGeometryEngine Create(Tolerance tolerance) =>
        CreateManagedBsp(tolerance);

    public Result<IMesh> CreateMesh(ImmutableArray<Vec3> vertices, ImmutableArray<int> triangles, MeshMetadata metadata) =>
        ImmutableMesh.Create(vertices, triangles, metadata);
}
