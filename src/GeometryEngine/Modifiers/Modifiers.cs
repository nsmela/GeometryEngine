using System.Runtime.InteropServices;
using GeometryEngine.Internal;
using GeometryEngine.Internal.Decimation;
using GeometryEngine.Internal.Native;
using GeometryEngine.Internal.Smoothing;
using GeometryEngine.Internal.Spatial;

namespace GeometryEngine.Modifiers;

/// <summary>Errors the modifier slices report.</summary>
internal static class ModifierErrors
{
    public static readonly Error NonFiniteParameter =
        new("Modifiers.NonFinite", "The operation's parameters are not finite.");

    public static readonly Error OffsetFailed =
        new("Modifiers.OffsetFailed", "The offset surface could not be meshed; it may have vanished entirely.");

    public static readonly Error TargetTooSmall =
        new("Modifiers.TargetTooSmall", "A decimation target needs at least four triangles, the fewest a closed surface can have.");

    public static readonly Error NegativeIterations =
        new("Modifiers.NegativeIterations", "An iteration count cannot be negative.");

    public static readonly Error StrengthOutOfRange =
        new("Modifiers.StrengthOutOfRange", "A smoothing strength belongs in (0, 1]; above one the filter amplifies roughness instead of removing it.");

    public static readonly Error NegativeParameter =
        new("Modifiers.NegativeParameter", "An angle or tolerance cannot be negative.");

    public static readonly Error DistanceBelowCell =
        new("Modifiers.DistanceBelowCell", "The smoothing distance is smaller than one grid cell, so the inflation cannot be resolved. Ask for a finer cell size or a larger distance.");
}

/// <summary>Ask for the surface lying a fixed distance from a mesh.</summary>
public sealed record OffsetRequest(IMesh Mesh, double Distance, double CellSize);

/// <summary>
/// Offsets by sampling a signed distance field over a box around the mesh and meshing its
/// isosurface with the native kernel's level-set mesher, whose output is a closed solid.
///
/// The field comes from the native libigl library where it is present - distance from an AABB
/// tree, sign from the fast winding number, the whole offset behind one call - and from the
/// managed BVH otherwise. The native route is both faster and the more robust on scans with
/// holes, where a pseudonormal sign has no consistent inside to consult. The result records
/// which ran.
/// </summary>
internal sealed class OffsetHandler
{
    /// <summary>Recorded on offsets whose field came from the native library.</summary>
    public const string NativeProducer = "GeometryEngine.Modifiers.Offset (native field)";

    /// <summary>Recorded on offsets whose field came from the managed BVH.</summary>
    public const string ManagedProducer = "GeometryEngine.Modifiers.Offset (managed field)";

    /// <summary>
    /// Cells across the longest side of the sampled volume when the caller leaves the cell size to
    /// the engine. High enough to keep a bolus recognisable, low enough to stay interactive.
    /// </summary>
    private const int DefaultResolution = 32;

    /// <summary>
    /// Ceiling on the cells sampled, whatever cell size is asked for. A caller passing a fine cell
    /// on a large model would otherwise ask for millions of queries - minutes of work. The native
    /// field affords a far larger budget than the managed one.
    /// </summary>
    private const double NativeCellBudget = 400_000;

    private const double ManagedCellBudget = 50_000;

    /// <summary>Padding beyond the offset distance, so the new surface is never clipped by the box.</summary>
    private const double BoxMarginCells = 2;

    public Result<IMesh> Handle(OffsetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (!double.IsFinite(request.Distance) || !double.IsFinite(request.CellSize))
        {
            return ModifierErrors.NonFiniteParameter;
        }

        var mesh = request.Mesh;
        var min = mesh.Vertices[0];
        var max = mesh.Vertices[0];
        foreach (var vertex in mesh.Vertices)
        {
            min = min.ComponentMin(vertex);
            max = max.ComponentMax(vertex);
        }

        var native = DistanceFieldNative.IsAvailable;
        var size = max - min;
        var longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var cell = request.CellSize > 0 ? request.CellSize : longest / DefaultResolution;
        if (!(cell > 0))
        {
            return MeshErrors.EmptyOperand;
        }

        // Coarsen rather than let a fine cell run the sample count away. Estimated over the padded
        // box, which is what is actually sampled.
        var budget = native ? NativeCellBudget : ManagedCellBudget;
        var padded = size + new Vec3(1, 1, 1) * (2 * (Math.Abs(request.Distance) + (BoxMarginCells * cell)));
        var cells = padded.X * padded.Y * padded.Z / Math.Pow(cell, 3);
        if (cells > budget)
        {
            cell *= Math.Cbrt(cells / budget);
        }

        var padding = new Vec3(1, 1, 1) * (Math.Abs(request.Distance) + (BoxMarginCells * cell));
        min -= padding;
        max += padding;

        var metadata = mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, native ? NativeProducer : ManagedProducer);

        if (native)
        {
            var result = OffsetNatively(mesh, request.Distance, min, max, cell, metadata);
            if (result.IsSuccess || result.Error != ManifoldErrors.Unavailable)
            {
                return result;
            }

            // Only an unloadable library falls through; anything else is a real answer.
            metadata = mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, ManagedProducer);
        }

        var bvh = new MeshBvh(mesh);
        var managed = ManifoldKernel.LevelSet(bvh.SignedDistance, min, max, cell, request.Distance, metadata);
        return managed.IsFailure && managed.Error == ManifoldErrors.EmptyResult
            ? ModifierErrors.OffsetFailed
            : managed;
    }

    private static Result<IMesh> OffsetNatively(IMesh mesh, double distance, Vec3 min, Vec3 max, double cell, MeshMetadata metadata)
    {
        NativeMesh output = default;
        try
        {
            var status = (DistanceFieldStatus)DistanceFieldNative.ge_offset(
                NativeMeshArrays.Flatten(mesh.Vertices),
                (nuint)mesh.VertexCount,
                [.. mesh.Triangles],
                (nuint)mesh.TriangleCount,
                distance,
                [min.X, min.Y, min.Z, max.X, max.Y, max.Z],
                cell,
                out output);

            return status switch
            {
                DistanceFieldStatus.Ok => Extract(output, metadata),
                DistanceFieldStatus.ManifoldUnavailable => ManifoldErrors.Unavailable,
                DistanceFieldStatus.LevelSetFailed => ModifierErrors.OffsetFailed,
                DistanceFieldStatus.EmptyMesh => MeshErrors.EmptyOperand,
                _ => new Error("Modifiers.NativeFailure", $"The native distance field failed with status {status}."),
            };
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return ManifoldErrors.Unavailable;
        }
        finally
        {
            if (output.Vertices != IntPtr.Zero || output.Triangles != IntPtr.Zero)
            {
                DistanceFieldNative.ge_mesh_free(ref output);
            }
        }
    }

    private static Result<IMesh> Extract(NativeMesh native, MeshMetadata metadata)
    {
        var vertexCount = (int)native.VertexCount;
        var triangleCount = (int)native.TriangleCount;
        if (vertexCount == 0 || triangleCount == 0)
        {
            return ModifierErrors.OffsetFailed;
        }

        // Vec3 is three doubles, laid out as the native buffer is, so each buffer is copied once,
        // straight into the array the mesh will hold, rather than through a staging array.
        var vertices = new Vec3[vertexCount];
        unsafe
        {
            new ReadOnlySpan<Vec3>((void*)native.Vertices, vertexCount).CopyTo(vertices);
        }

        var triangles = new int[triangleCount * 3];
        Marshal.Copy(native.Triangles, triangles, 0, triangles.Length);

        return ImmutableMesh.Create(
            ImmutableCollectionsMarshal.AsImmutableArray(vertices),
            ImmutableCollectionsMarshal.AsImmutableArray(triangles),
            metadata);
    }
}

/// <summary>Ask for a mesh offset out and back again.</summary>
public sealed record DoubleOffsetRequest(IMesh Mesh, double Distance, int Iterations, double CellSize);

/// <summary>
/// Out by the distance, then back by the same. The round trip rounds away concave detail
/// smaller than the distance - a morphological closing - which is what smoothing wants.
/// </summary>
internal sealed class DoubleOffsetHandler(OffsetHandler offset)
{
    private readonly OffsetHandler _offset = offset;

    public Result<IMesh> Handle(DoubleOffsetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var current = request.Mesh;
        for (var i = 0; i < request.Iterations; i++)
        {
            var grown = _offset.Handle(new OffsetRequest(current, request.Distance, request.CellSize));
            if (grown.IsFailure)
            {
                return i == 0 ? grown : Result.Success(current);
            }

            var shrunk = _offset.Handle(new OffsetRequest(grown.Value, -request.Distance, request.CellSize));
            if (shrunk.IsFailure)
            {
                return i == 0 ? shrunk : Result.Success(current);
            }

            current = shrunk.Value;
        }

        return Result.Success(current);
    }
}

/// <summary>Ask for a mesh reduced towards a triangle count.</summary>
public sealed record DecimateRequest(IMesh Mesh, int TargetTriangleCount);

internal sealed class DecimateHandler
{
    public Result<IMesh> Handle(DecimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (request.TargetTriangleCount < 4)
        {
            return ModifierErrors.TargetTooSmall;
        }

        var (vertices, triangles) = MeshDecimator.Decimate(
            request.Mesh, request.TargetTriangleCount, MeshCleanup.RelativeTolerance(request.Mesh));

        return ImmutableMesh.Create(vertices, triangles, request.Mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, "GeometryEngine.Modifiers.Decimate"));
    }
}

/// <summary>Ask for a mesh's redundant geometry to be cleared away.</summary>
public sealed record RepairRequest(IMesh Mesh);

/// <summary>
/// Welds at a tolerance scaled to the model, then drops what describes no surface: faces whose
/// corners welded together, faces with no area, repeated faces, and vertices left unused.
/// </summary>
internal sealed class RepairHandler
{
    public Result<IMesh> Handle(RepairRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var tolerance = MeshCleanup.RelativeTolerance(request.Mesh);
        var (vertices, welded) = MeshCleanup.Weld(request.Mesh.Vertices, request.Mesh.Triangles, tolerance);

        var areaFloor = tolerance * tolerance;
        var withArea = new List<int>(welded.Count);
        for (var i = 0; i + 2 < welded.Count; i += 3)
        {
            var a = vertices[welded[i]];
            var b = vertices[welded[i + 1]];
            var c = vertices[welded[i + 2]];
            if ((b - a).Cross(c - a).Length * 0.5 > areaFloor)
            {
                withArea.Add(welded[i]);
                withArea.Add(welded[i + 1]);
                withArea.Add(welded[i + 2]);
            }
        }

        var (compactVertices, compactTriangles) = MeshCleanup.Compact(vertices, MeshCleanup.DropRepeatedFaces(withArea));

        return ImmutableMesh.Create(compactVertices, compactTriangles, request.Mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, "GeometryEngine.Modifiers.Repair"));
    }
}

/// <summary>Ask for surfaces passing through each other to be re-cut.</summary>
public sealed record RepairSelfIntersectionsRequest(IMesh Mesh);

/// <summary>
/// Separates the mesh into its shells and unions them back together through the native kernel,
/// which re-cuts every surface where one shell passes through another.
///
/// A self-union of the whole mesh is not the same thing, and gets overlapping shells wrong: the
/// kernel reads its input as one solid, so the region two shells share is enclosed twice, reads
/// as inside-out, and is carved away rather than kept. Unioning shell by shell asks the question
/// the kernel is built to answer. A mesh the kernel will not accept comes back unchanged rather
/// than failing a pipeline over it.
/// </summary>
internal sealed class RepairSelfIntersectionsHandler
{
    private const string Producer = "GeometryEngine.Modifiers.RepairSelfIntersections";

    private readonly Evaluators.ComponentsHandler _components = new();

    public Result<IMesh> Handle(RepairSelfIntersectionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        var metadata = request.Mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, Producer);

        // The unchanged path hands back the very geometry it was given, so anything the caller
        // had annotated it with still describes it: only the producer changes.
        var unchanged = Result.Success(request.Mesh.WithMetadata(
            request.Mesh.Metadata.WithCreatedBy($"{Producer} (unchanged)")));

        var shells = _components.Handle(new Evaluators.ComponentsRequest(request.Mesh));
        if (shells.IsFailure)
        {
            return unchanged;
        }

        if (shells.Value.Length == 1)
        {
            // One shell passing through itself: the kernel's boolean re-cuts it on the way through.
            var resolved = ManifoldKernel.Union(shells.Value[0], shells.Value[0], metadata);
            return resolved.IsSuccess ? Result.Success(resolved.Value.Mesh) : unchanged;
        }

        // Every shell in one native union. Folding them in one at a time sent the growing result
        // across the boundary and back once per shell - quadratic in the shell count.
        var union = ManifoldKernel.Batch(shells.Value, ManifoldOpType.Add, metadata);
        return union.IsSuccess ? Result.Success(union.Value.Mesh.WithMetadata(metadata)) : unchanged;
    }
}

/// <summary>Ask for a mesh's high-frequency detail filtered away, leaving its shape.</summary>
public sealed record LaplacianSmoothRequest(IMesh Mesh, int Iterations, double Strength);

/// <summary>
/// Taubin λ|μ fairing. Moves vertices and never touches connectivity, so the triangle count,
/// the watertightness and any tear in the input all survive unchanged - which is the main thing
/// separating it from the offset route, where the surface is thrown away and re-meshed.
/// </summary>
internal sealed class LaplacianSmoothHandler
{
    /// <summary>
    /// Above this, λ overshoots: a vertex is thrown past the centroid of its neighbours and the
    /// filter amplifies the roughness it was asked to remove.
    /// </summary>
    private const double MaximumStrength = 1.0;

    public Result<IMesh> Handle(LaplacianSmoothRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (request.Iterations < 0)
        {
            return ModifierErrors.NegativeIterations;
        }

        if (!double.IsFinite(request.Strength))
        {
            return ModifierErrors.NonFiniteParameter;
        }

        if (request.Strength <= 0 || request.Strength > MaximumStrength)
        {
            return ModifierErrors.StrengthOutOfRange;
        }

        var (vertices, triangles) = LaplacianSmoother.Smooth(
            request.Mesh, request.Iterations, request.Strength);

        return ImmutableMesh.Create(
            vertices,
            triangles,
            request.Mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, "GeometryEngine.Modifiers.LaplacianSmooth"));
    }
}

/// <summary>Ask for a mesh inflated and deflated, rounding away concave detail.</summary>
public sealed record OffsetSmoothRequest(IMesh Mesh, double Distance, int Iterations, double CellSize);

/// <summary>
/// Morphological closing, iterated entirely on a sampled distance field: the mesh is consulted
/// once to build the field and produced once at the end, however many rounds run in between.
///
/// This is what separates it from <see cref="DoubleOffsetHandler"/>, which re-meshes twice per
/// iteration and feeds each result back in as the next round's input. That costs two level-set
/// meshings and two field builds per round, and compounds the error: every pass resamples a
/// surface that was already a resampling, so detail is lost to the grid repeatedly instead of
/// once. Here the grid is the state, and the only resampling is the final mesh - measured on a
/// bolus, DoubleOffset's volume drifts -1.0 %, -2.3 %, -3.5 % over one to three rounds while
/// this holds near +3.7 %, which is the direction a closing should move in.
/// </summary>
internal sealed class OffsetSmoothHandler(GridSampling sampling = OffsetSmoothHandler.Default)
{
    /// <summary>How the engine fills the grid unless a test or a measurement asks otherwise.</summary>
    internal const GridSampling Default = GridSampling.EveryNode;

    /// <summary>Cells across the longest side when the caller leaves the cell size to the engine.</summary>
    private const int DefaultResolution = 64;

    /// <summary>
    /// Ceiling on grid nodes. Higher than the offset handler's, because the cost here is not per
    /// sample through a P/Invoke: the field is sampled once and the transforms that follow are
    /// linear in the node count.
    /// </summary>
    private const double CellBudget = 1_500_000;

    /// <summary>Padding beyond the inflation distance, so the grown surface is never clipped by the box.</summary>
    private const double BoxMarginCells = 3;

    public Result<IMesh> Handle(OffsetSmoothRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (request.Iterations < 0)
        {
            return ModifierErrors.NegativeIterations;
        }

        if (!double.IsFinite(request.Distance) || !double.IsFinite(request.CellSize))
        {
            return ModifierErrors.NonFiniteParameter;
        }

        var mesh = request.Mesh;
        var metadata = mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, "GeometryEngine.Modifiers.OffsetSmooth");

        // Nothing asked for means nothing done, rather than a needless round trip through the
        // grid that would return the mesh re-meshed and slightly different. The geometry is
        // untouched, so the caller's annotations survive intact rather than being carried.
        if (request.Iterations == 0 || request.Distance == 0)
        {
            return Result.Success(mesh.WithMetadata(
                mesh.Metadata.WithCreatedBy("GeometryEngine.Modifiers.OffsetSmooth")));
        }

        var distance = Math.Abs(request.Distance);

        var planned = GridFor(mesh, distance, request.CellSize);
        if (planned.IsFailure)
        {
            return planned.Error;
        }

        var (min, max, cell, reach) = planned.Value;

        var bvh = new MeshBvh(mesh);
        var grid = sampling == GridSampling.NearSurface && CanSampleNearSurface(mesh)
            ? SignedDistanceGrid.SampleNear(bvh, min, max, cell, reach)
            : SignedDistanceGrid.Sample(bvh.SignedDistance, min, max, cell);

        for (var i = 0; i < request.Iterations; i++)
        {
            // Inflate, then recover a true field from the grown surface - see Reinitialise for
            // why the recovery is not optional. Then deflate and recover again.
            grid.Shift(distance);
            grid.Reinitialise();
            grid.Shift(-distance);
            grid.Reinitialise();
        }

        var result = ManifoldKernel.LevelSet(grid.Sample, min, max, cell, 0, metadata);
        return result.IsFailure && result.Error == ManifoldErrors.EmptyResult
            ? ModifierErrors.OffsetFailed
            : result;
    }

    /// <summary>
    /// The box and cell a closing by <paramref name="distance"/> is sampled on, and how far from
    /// the surface the closing can ever read an exact distance.
    /// </summary>
    internal static Result<ClosingGrid> GridFor(IMesh mesh, double distance, double cellSize)
    {
        var min = mesh.Vertices[0];
        var max = mesh.Vertices[0];
        foreach (var vertex in mesh.Vertices)
        {
            min = min.ComponentMin(vertex);
            max = max.ComponentMax(vertex);
        }

        var size = max - min;
        var longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var cell = cellSize > 0 ? cellSize : longest / DefaultResolution;
        if (!(cell > 0))
        {
            return MeshErrors.EmptyOperand;
        }

        var padded = size + (new Vec3(1, 1, 1) * (2 * (distance + (BoxMarginCells * cell))));
        var nodes = padded.X * padded.Y * padded.Z / Math.Pow(cell, 3);
        if (nodes > CellBudget)
        {
            cell *= Math.Cbrt(nodes / CellBudget);
        }

        // The inflation has to be resolvable on the grid, or the shift moves the zero level clean
        // past every node that could hold the crossing and the surface disappears rather than
        // rounding. Saying so beats returning an empty mesh and letting the caller guess.
        if (distance < cell)
        {
            return ModifierErrors.DistanceBelowCell;
        }

        var reach = distance + (BoxMarginCells * cell);
        var padding = new Vec3(1, 1, 1) * reach;
        return new ClosingGrid(min - padding, max + padding, cell, reach);
    }

    /// <summary>
    /// Whether the mesh is one whose far side can be told without measuring it. Not yet decided:
    /// see the tests.
    /// </summary>
    internal static bool CanSampleNearSurface(IMesh mesh) => false;
}

/// <summary>How the distance grid behind a closing is filled from the mesh.</summary>
internal enum GridSampling
{
    /// <summary>Every node is asked for its exact distance to the surface.</summary>
    EveryNode,

    /// <summary>
    /// Nodes are asked only whether the surface is within the closing's reach; those beyond it
    /// take the side of the node before them.
    /// </summary>
    NearSurface,
}

/// <summary>The grid a closing is sampled on.</summary>
/// <param name="Reach">
/// How far from the surface the closing can read an exact distance: the inflation plus the
/// margin. The box is padded by exactly this much.
/// </param>
internal readonly record struct ClosingGrid(Vec3 Min, Vec3 Max, double Cell, double Reach);

/// <summary>Ask for a mesh's creases rounded and its flat surface left alone.</summary>
public sealed record SmoothEdgesRequest(IMesh Mesh, double KeepSharperThan, double Tolerance);

/// <summary>
/// Subdivides the surface into a smooth interpolation of itself, by sharing vertex normals across
/// every edge shallower than the threshold, turning those into tangents, and refining through
/// them. Manifold does the work; this validates and scales the tolerance.
///
/// Half of it behaves as hoped and half does not, which the interface documents with figures.
/// Flat surface is preserved exactly and costs nothing: a patch through coplanar vertices with
/// in-plane tangents is planar, and a cube's faces come back untouched and unsubdivided. But
/// acting on a crease bulges its whole neighbourhood outwards rather than rounding the corner -
/// a coarse cube gains 168 % of its volume, a boolean mould 209 % - because the interpolated
/// surface only has to pass through the existing vertices, and between them it is free to swing
/// as wide as their spacing allows. The threshold is therefore a cliff rather than a dial: below
/// a model's real edge angles it does nothing, above them it inflates.
/// </summary>
internal sealed class SmoothEdgesHandler
{
    /// <summary>
    /// Tolerance as a fraction of the bounding diagonal when the caller names none. The
    /// interpolated surface is refined until it sits within this of the ideal, so it trades
    /// triangles for fidelity and needs to be scaled to the model rather than fixed.
    /// </summary>
    private const double DefaultToleranceFraction = 0.0005;

    public Result<IMesh> Handle(SmoothEdgesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (!double.IsFinite(request.KeepSharperThan) || !double.IsFinite(request.Tolerance))
        {
            return ModifierErrors.NonFiniteParameter;
        }

        if (request.KeepSharperThan < 0 || request.Tolerance < 0)
        {
            return ModifierErrors.NegativeParameter;
        }

        var mesh = request.Mesh;
        var tolerance = request.Tolerance;
        if (tolerance == 0)
        {
            var min = mesh.Vertices[0];
            var max = mesh.Vertices[0];
            foreach (var vertex in mesh.Vertices)
            {
                min = min.ComponentMin(vertex);
                max = max.ComponentMax(vertex);
            }

            tolerance = (max - min).Length * DefaultToleranceFraction;
        }

        var metadata = mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, "GeometryEngine.Modifiers.SmoothEdges");
        var outcome = ManifoldKernel.SmoothEdges(mesh, request.KeepSharperThan, tolerance, metadata);

        return outcome.IsSuccess
            ? Result.Success(outcome.Value.Mesh)
            : Result.Failure<IMesh>(outcome.Error);
    }
}

/// <summary>Ask for a mesh's creases rounded and everything else left exactly alone.</summary>
public sealed record SmoothCreasesRequest(
    IMesh Mesh, double RoundSharperThan, double MaxDeviation, int Iterations, double Strength);

/// <summary>
/// Taubin fairing restricted to the neighbourhood of a fold and bounded by a tolerance band. The
/// work is in <see cref="CreaseSmoother"/>; this validates and records what happened.
/// </summary>
internal sealed class SmoothCreasesHandler
{
    private const double MaximumStrength = 1.0;

    public Result<IMesh> Handle(SmoothCreasesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        if (request.Iterations < 0)
        {
            return ModifierErrors.NegativeIterations;
        }

        if (!double.IsFinite(request.RoundSharperThan)
            || !double.IsFinite(request.MaxDeviation)
            || !double.IsFinite(request.Strength))
        {
            return ModifierErrors.NonFiniteParameter;
        }

        if (request.RoundSharperThan < 0 || request.MaxDeviation < 0)
        {
            return ModifierErrors.NegativeParameter;
        }

        if (request.Strength <= 0 || request.Strength > MaximumStrength)
        {
            return ModifierErrors.StrengthOutOfRange;
        }

        var smoothed = CreaseSmoother.Smooth(
            request.Mesh,
            request.RoundSharperThan,
            request.MaxDeviation,
            request.Iterations,
            request.Strength);

        // GeometryEngine's metadata carries a name, a producer and the caller's own annotations,
        // with nowhere for the engine to report the crease and vertex counts. A caller that needs
        // them can compare the vertices, and where nothing qualified they are identical.
        var metadata = request.Mesh.Metadata.CarriedThrough(MeshOperation.Rebuild, "GeometryEngine.Modifiers.SmoothCreases");

        return ImmutableMesh.Create(smoothed.Vertices, smoothed.Triangles, metadata);
    }
}

/// <summary>The <see cref="IGeometryModifiers"/> facade over the modifier slices.</summary>
internal sealed class GeometryModifiers : IGeometryModifiers
{
    private readonly OffsetHandler _offset = new();
    private readonly DoubleOffsetHandler _doubleOffset;
    private readonly DecimateHandler _decimate = new();
    private readonly RepairHandler _repair = new();
    private readonly RepairSelfIntersectionsHandler _selfIntersections = new();
    private readonly LaplacianSmoothHandler _laplacian = new();
    private readonly OffsetSmoothHandler _offsetSmooth = new();
    private readonly SmoothEdgesHandler _smoothEdges = new();
    private readonly SmoothCreasesHandler _smoothCreases = new();

    public GeometryModifiers()
    {
        _doubleOffset = new DoubleOffsetHandler(_offset);
    }

    public Result<IMesh> LaplacianSmooth(IMesh mesh, int iterations = 5, double strength = 0.5) =>
        _laplacian.Handle(new LaplacianSmoothRequest(mesh, iterations, strength));

    public Result<IMesh> OffsetSmooth(IMesh mesh, double distance, int iterations = 1, double cellSize = 0) =>
        _offsetSmooth.Handle(new OffsetSmoothRequest(mesh, distance, iterations, cellSize));

    public Result<IMesh> SmoothEdges(IMesh mesh, double keepSharperThan = 30, double tolerance = 0) =>
        _smoothEdges.Handle(new SmoothEdgesRequest(mesh, keepSharperThan, tolerance));

    public Result<IMesh> SmoothCreases(
        IMesh mesh,
        double roundSharperThan = 30,
        double maxDeviation = 0.25,
        int iterations = 10,
        double strength = 0.5) =>
        _smoothCreases.Handle(
            new SmoothCreasesRequest(mesh, roundSharperThan, maxDeviation, iterations, strength));

    public Result<IMesh> Offset(IMesh mesh, double distance, double cellSize = 0) =>
        _offset.Handle(new OffsetRequest(mesh, distance, cellSize));

    public Result<IMesh> DoubleOffset(IMesh mesh, double distance, int iterations = 1, double cellSize = 0) =>
        _doubleOffset.Handle(new DoubleOffsetRequest(mesh, distance, iterations, cellSize));

    public Result<IMesh> Decimate(IMesh mesh, int targetTriangleCount) =>
        _decimate.Handle(new DecimateRequest(mesh, targetTriangleCount));

    public Result<IMesh> Repair(IMesh mesh) => _repair.Handle(new RepairRequest(mesh));

    public Result<IMesh> RepairSelfIntersections(IMesh mesh) =>
        _selfIntersections.Handle(new RepairSelfIntersectionsRequest(mesh));
}
