using System.Collections.Immutable;
using System.Runtime.InteropServices;
using BasicResults;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;

namespace GeometryEngine.Internal.Native;

/// <summary>How a result was produced, so a caller can tell what it is holding.</summary>
internal enum ManifoldProvenance
{
    /// <summary>Both operands were valid 2-manifolds; the result is guaranteed watertight.</summary>
    Native,

    /// <summary>
    /// An operand was not a valid 2-manifold and was welded by Manifold's own merge before
    /// the operation. The result is still guaranteed watertight, but it describes geometry
    /// that differs from the input.
    /// </summary>
    NativeAfterMergingOperands,
}

/// <summary>The outcome of a native operation, with the provenance of the mesh it produced.</summary>
internal readonly record struct ManifoldOutcome(IMesh Mesh, ManifoldProvenance Provenance);

/// <summary>
/// Executes Boolean operations through the native Manifold library.
/// Handles marshaling to and from <see cref="IMesh"/> and ensures every native
/// resource is deterministically freed, preventing native leaks.
///
/// No failure escapes as an exception. The library's contract is that every failure is a
/// value, and a P/Invoke breaks that contract loudly: a missing or mismatched native binary
/// raises <see cref="DllNotFoundException"/> or <see cref="EntryPointNotFoundException"/>
/// from the first call, which would otherwise tear down the caller rather than be reported.
/// </summary>
internal static unsafe class ManifoldKernel
{
    public static Result<ManifoldOutcome> Union(IMesh left, IMesh right, MeshMetadata metadata) =>
        RunOperation(left, right, metadata, ManifoldNative.manifold_union);

    public static Result<ManifoldOutcome> Subtract(IMesh left, IMesh right, MeshMetadata metadata) =>
        RunOperation(left, right, metadata, ManifoldNative.manifold_difference);

    public static Result<ManifoldOutcome> Intersect(IMesh left, IMesh right, MeshMetadata metadata) =>
        RunOperation(left, right, metadata, ManifoldNative.manifold_intersection);

    /// <summary>
    /// Re-meshes the isosurface of a signed distance field lying <paramref name="level"/> from
    /// the surface - the managed-field route to an offset, used where the native distance field
    /// is unavailable.
    /// </summary>
    /// <param name="signedDistance">Distance to the surface, negative inside the solid. Called from several threads at once.</param>
    public static Result<IMesh> LevelSet(
        Func<Vec3, double> signedDistance,
        Vec3 min,
        Vec3 max,
        double edgeLength,
        double level,
        MeshMetadata metadata) =>
        Guarded(() =>
        {
            // Manifold keeps the region where the field is above the level, so the field is
            // handed over negated: positive inside the solid.
            ManifoldNative.SdfCallback callback = (x, y, z, _) => -signedDistance(new Vec3(x, y, z));

            var box = ManifoldNative.manifold_alloc_box();
            var solid = IntPtr.Zero;
            try
            {
                ManifoldNative.manifold_box(box, min.X, min.Y, min.Z, max.X, max.Y, max.Z);

                solid = ManifoldNative.manifold_alloc_manifold();
                ManifoldNative.manifold_level_set(solid, callback, box, edgeLength, -level, 0, IntPtr.Zero);

                // The delegate must outlive the native call; without this the JIT is free to
                // collect it while Manifold is still sampling through it.
                GC.KeepAlive(callback);

                var status = ManifoldNative.manifold_status(solid);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.OperationFailed(status));
                }

                return ManifoldNative.manifold_num_tri(solid) == 0
                    ? Result.Failure<IMesh>(ManifoldErrors.EmptyResult)
                    : FromManifold(solid, metadata);
            }
            finally
            {
                if (solid != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(solid);
                }

                ManifoldNative.manifold_delete_box(box);
            }
        });

    /// <summary>
    /// Extrudes planar contours into a closed solid between two heights. Manifold triangulates
    /// the caps and closes the walls itself, so the result is manifold by construction.
    /// </summary>
    /// <param name="contours">Outer boundaries wound counter-clockwise, holes clockwise.</param>
    public static Result<IMesh> Extrude(
        IReadOnlyList<IReadOnlyList<Vec2>> contours,
        double zMin,
        double zMax,
        MeshMetadata metadata) =>
        Guarded(() =>
        {
            var simplePolygons = new List<IntPtr>(contours.Count);
            var polygons = IntPtr.Zero;
            var extruded = IntPtr.Zero;
            var placed = IntPtr.Zero;

            try
            {
                foreach (var contour in contours)
                {
                    if (contour.Count < 3)
                    {
                        continue;
                    }

                    var points = new ManifoldNative.NativeVec2[contour.Count];
                    for (var i = 0; i < contour.Count; i++)
                    {
                        points[i] = new ManifoldNative.NativeVec2 { X = contour[i].X, Y = contour[i].Y };
                    }

                    var polygon = ManifoldNative.manifold_alloc_simple_polygon();
                    fixed (ManifoldNative.NativeVec2* pPoints = points)
                    {
                        ManifoldNative.manifold_simple_polygon(polygon, pPoints, (nuint)points.Length);
                    }

                    simplePolygons.Add(polygon);
                }

                if (simplePolygons.Count == 0)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.EmptyOperand("extrusion contours"));
                }

                var handles = simplePolygons.ToArray();
                polygons = ManifoldNative.manifold_alloc_polygons();
                fixed (IntPtr* pHandles = handles)
                {
                    ManifoldNative.manifold_polygons(polygons, pHandles, (nuint)handles.Length);
                }

                extruded = ManifoldNative.manifold_alloc_manifold();
                ManifoldNative.manifold_extrude(extruded, polygons, zMax - zMin, 0, 0, 1, 1);

                var status = ManifoldNative.manifold_status(extruded);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.OperationFailed(status));
                }

                if (ManifoldNative.manifold_num_tri(extruded) == 0)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.EmptyResult);
                }

                // Extrusion always starts at z = 0; lift it onto the requested range.
                placed = ManifoldNative.manifold_alloc_manifold();
                ManifoldNative.manifold_translate(placed, extruded, 0, 0, zMin);

                return FromManifold(placed, metadata);
            }
            finally
            {
                if (placed != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(placed);
                }

                if (extruded != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(extruded);
                }

                if (polygons != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_polygons(polygons);
                }

                foreach (var polygon in simplePolygons)
                {
                    ManifoldNative.manifold_delete_simple_polygon(polygon);
                }
            }
        });

    /// <summary>Runs a native operation, turning the exceptions a P/Invoke can raise into failures.</summary>
    private static Result<T> Guarded<T>(Func<Result<T>> operation)
    {
        if (!ManifoldNative.IsAvailable)
        {
            return Result.Failure<T>(ManifoldErrors.Unavailable);
        }

        try
        {
            return operation();
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            return Result.Failure<T>(ManifoldErrors.Unusable(exception));
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or MarshalDirectiveException)
        {
            return Result.Failure<T>(ManifoldErrors.BindingMismatch(exception));
        }
    }

    private delegate IntPtr NativeBooleanOp(IntPtr mem, IntPtr a, IntPtr b);

    private static Result<ManifoldOutcome> RunOperation(
        IMesh left,
        IMesh right,
        MeshMetadata metadata,
        NativeBooleanOp op)
    {
        if (!ManifoldNative.IsAvailable)
        {
            return Result.Failure<ManifoldOutcome>(ManifoldErrors.Unavailable);
        }

        try
        {
            return RunOperationCore(left, right, metadata, op);
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            // The library is absent or unusable on this machine. Ordinary on a platform
            // whose binaries do not ship, and a caller with a managed fallback should use it.
            return Result.Failure<ManifoldOutcome>(ManifoldErrors.Unusable(exception));
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or MarshalDirectiveException)
        {
            // The library loaded but does not have the shape this binding expects - a wrong
            // export name, or a signature that cannot be marshalled. That is a defect in
            // this code or a version mismatch, not a platform limitation, and it is reported
            // as its own failure so it cannot be mistaken for one: quietly substituting a
            // different kernel would hide exactly the bug that needs fixing.
            return Result.Failure<ManifoldOutcome>(ManifoldErrors.BindingMismatch(exception));
        }
    }

    private static Result<ManifoldOutcome> RunOperationCore(
        IMesh left,
        IMesh right,
        MeshMetadata metadata,
        NativeBooleanOp op)
    {
        var leftManifoldResult = ToManifold(left);
        if (leftManifoldResult.IsFailure)
        {
            return Result.Failure<ManifoldOutcome>(leftManifoldResult.Error);
        }

        var rightManifoldResult = ToManifold(right);
        if (rightManifoldResult.IsFailure)
        {
            ManifoldNative.manifold_delete_manifold(leftManifoldResult.Value.Handle);
            return Result.Failure<ManifoldOutcome>(rightManifoldResult.Error);
        }

        var leftPtr = leftManifoldResult.Value.Handle;
        var rightPtr = rightManifoldResult.Value.Handle;
        var resultPtr = IntPtr.Zero;

        var provenance = leftManifoldResult.Value.Merged || rightManifoldResult.Value.Merged
            ? ManifoldProvenance.NativeAfterMergingOperands
            : ManifoldProvenance.Native;

        try
        {
            resultPtr = ManifoldNative.manifold_alloc_manifold();
            op(resultPtr, leftPtr, rightPtr);

            var status = ManifoldNative.manifold_status(resultPtr);
            if (status != ManifoldError.NoError)
            {
                return Result.Failure<ManifoldOutcome>(ManifoldErrors.OperationFailed(status));
            }

            var mesh = FromManifold(resultPtr, metadata);
            return mesh.IsFailure
                ? Result.Failure<ManifoldOutcome>(mesh.Error)
                : Result.Success(new ManifoldOutcome(mesh.Value, provenance));
        }
        finally
        {
            if (resultPtr != IntPtr.Zero)
            {
                ManifoldNative.manifold_delete_manifold(resultPtr);
            }

            ManifoldNative.manifold_delete_manifold(leftPtr);
            ManifoldNative.manifold_delete_manifold(rightPtr);
        }
    }

    /// <summary>A live native manifold, and whether its geometry had to be welded to build it.</summary>
    private readonly record struct Operand(IntPtr Handle, bool Merged);

    private static Result<Operand> ToManifold(IMesh mesh)
    {
        // An empty operand cannot be represented here. manifold_alloc_manifold only
        // allocates storage - it does not construct a Manifold - so handing the raw pointer
        // back would put uninitialised memory into a native boolean and then through a
        // native destructor. Callers reject empty operands before reaching this point;
        // saying so as a value keeps that guarantee local instead of assumed.
        if (mesh.VertexCount == 0 || mesh.TriangleCount == 0)
        {
            return Result.Failure<Operand>(ManifoldErrors.EmptyOperand(mesh.Metadata.Name));
        }

        var vertProps = new double[mesh.VertexCount * 3];
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var v = mesh.Vertices[i];
            vertProps[i * 3] = v.X;
            vertProps[(i * 3) + 1] = v.Y;
            vertProps[(i * 3) + 2] = v.Z;
        }

        var triVerts = new ulong[mesh.TriangleCount * 3];
        for (var i = 0; i < mesh.Triangles.Length; i++)
        {
            triVerts[i] = (ulong)mesh.Triangles[i];
        }

        fixed (double* pVerts = vertProps)
        fixed (ulong* pTris = triVerts)
        {
            var meshGl = ManifoldNative.manifold_alloc_meshgl64();
            try
            {
                ManifoldNative.manifold_meshgl64(
                    meshGl,
                    pVerts,
                    (nuint)mesh.VertexCount,
                    3,
                    pTris,
                    (nuint)mesh.TriangleCount);

                var manifold = ManifoldNative.manifold_alloc_manifold();
                ManifoldNative.manifold_of_meshgl64(manifold, meshGl);

                var status = ManifoldNative.manifold_status(manifold);
                var merged = false;

                if (status != ManifoldError.NoError)
                {
                    // The mesh is not a 2-manifold as supplied. Manifold's merge welds
                    // near-coincident vertices, which closes the common case of a surface
                    // exported with unshared vertices. It also *alters the geometry*, so the
                    // caller is told: see ManifoldProvenance.
                    ManifoldNative.manifold_delete_manifold(manifold);
                    var mergedMeshGl = ManifoldNative.manifold_alloc_meshgl64();
                    try
                    {
                        ManifoldNative.manifold_meshgl64_merge(mergedMeshGl, meshGl);
                        manifold = ManifoldNative.manifold_alloc_manifold();
                        ManifoldNative.manifold_of_meshgl64(manifold, mergedMeshGl);
                        status = ManifoldNative.manifold_status(manifold);
                        merged = status == ManifoldError.NoError;
                    }
                    finally
                    {
                        ManifoldNative.manifold_delete_meshgl64(mergedMeshGl);
                    }
                }

                if (status != ManifoldError.NoError)
                {
                    ManifoldNative.manifold_delete_manifold(manifold);
                    return Result.Failure<Operand>(ManifoldErrors.InvalidMesh(mesh.Metadata.Name, status));
                }

                return Result.Success(new Operand(manifold, merged));
            }
            finally
            {
                ManifoldNative.manifold_delete_meshgl64(meshGl);
            }
        }
    }

    private static Result<IMesh> FromManifold(IntPtr manifold, MeshMetadata metadata)
    {
        if (ManifoldNative.manifold_is_empty(manifold) != 0)
        {
            return ImmutableMesh.Create(ImmutableArray<Vec3>.Empty, ImmutableArray<int>.Empty, metadata);
        }

        var meshGl = ManifoldNative.manifold_alloc_meshgl64();
        try
        {
            ManifoldNative.manifold_get_meshgl64(meshGl, manifold);

            var nVerts = (int)ManifoldNative.manifold_meshgl64_num_vert(meshGl);
            var nTris = (int)ManifoldNative.manifold_meshgl64_num_tri(meshGl);
            var vertPropsLen = (int)ManifoldNative.manifold_meshgl64_vert_properties_length(meshGl);
            var triVertsLen = (int)ManifoldNative.manifold_meshgl64_tri_length(meshGl);

            if (nVerts == 0 || nTris == 0)
            {
                return ImmutableMesh.Create(ImmutableArray<Vec3>.Empty, ImmutableArray<int>.Empty, metadata);
            }

            // Vertex properties are interleaved, and the position is only the first three of
            // however many a vertex carries. The stride is read from the library rather than
            // assumed to be three, so extra properties (normals, colours) cannot be silently
            // read as coordinates - which would yield a plausible-looking but wrong mesh.
            var stride = (int)ManifoldNative.manifold_meshgl64_num_prop(meshGl);
            if (stride < 3)
            {
                return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                    $"vertices carry {stride} properties, too few to hold a position"));
            }

            // Cross-check the stride against the buffer the library will fill, so a
            // disagreement between the two is reported rather than read out of bounds.
            if (vertPropsLen != nVerts * stride)
            {
                return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                    $"vertex property buffer of {vertPropsLen} does not hold {nVerts} vertices of {stride} properties"));
            }

            if (triVertsLen != nTris * 3)
            {
                return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                    $"triangle index buffer of {triVertsLen} does not describe {nTris} triangles"));
            }

            var outVertProps = new double[vertPropsLen];
            var outTriVerts = new ulong[triVertsLen];
            var mergeLength = (int)ManifoldNative.manifold_meshgl64_merge_length(meshGl);
            var mergeFrom = new ulong[mergeLength];
            var mergeTo = new ulong[mergeLength];

            fixed (double* pOutVerts = outVertProps)
            fixed (ulong* pOutTris = outTriVerts)
            fixed (ulong* pMergeFrom = mergeFrom)
            fixed (ulong* pMergeTo = mergeTo)
            {
                ManifoldNative.manifold_meshgl64_vert_properties((IntPtr)pOutVerts, meshGl);
                ManifoldNative.manifold_meshgl64_tri_verts((IntPtr)pOutTris, meshGl);

                if (mergeLength > 0)
                {
                    ManifoldNative.manifold_meshgl64_merge_from_vert((IntPtr)pMergeFrom, meshGl);
                    ManifoldNative.manifold_meshgl64_merge_to_vert((IntPtr)pMergeTo, meshGl);
                }
            }

            var vertices = new List<Vec3>(nVerts);
            for (var i = 0; i < nVerts; i++)
            {
                vertices.Add(new Vec3(
                    outVertProps[i * stride],
                    outVertProps[(i * stride) + 1],
                    outVertProps[(i * stride) + 2]));
            }

            // Manifold splits a position into several vertices wherever runs from different
            // operands meet at it, and records the split in the merge vectors rather than the
            // index buffer. Read as-is, every such seam is a ring of boundary edges: the solid is
            // closed, but its triangles do not say so. Applying the merge is what makes the
            // guarantee visible to everything downstream that pairs edges by index.
            var remap = new int[nVerts];
            for (var i = 0; i < nVerts; i++)
            {
                remap[i] = i;
            }

            for (var i = 0; i < mergeLength; i++)
            {
                if (mergeFrom[i] >= (ulong)nVerts || mergeTo[i] >= (ulong)nVerts)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                        $"merge pair {i} refers past the {nVerts} vertices"));
                }

                remap[mergeFrom[i]] = (int)mergeTo[i];
            }

            var triangles = new List<int>(triVertsLen);
            for (var i = 0; i < triVertsLen; i++)
            {
                triangles.Add(remap[outTriVerts[i]]);
            }

            if (mergeLength == 0)
            {
                return ImmutableMesh.Create([.. vertices], [.. triangles], metadata);
            }

            // Merged-away vertices are left unreferenced; drop them.
            var (compactVertices, compactTriangles) = MeshCleanup.Compact(vertices, triangles);
            return ImmutableMesh.Create(compactVertices, compactTriangles, metadata);
        }
        finally
        {
            ManifoldNative.manifold_delete_meshgl64(meshGl);
        }
    }
}

/// <summary>Failures the native kernel can report.</summary>
internal static class ManifoldErrors
{
    public static readonly Error Unavailable = new(
        "Manifold.Unavailable",
        "The native Manifold library is not available for this platform or deployment. " +
        "Prebuilt binaries ship for win-x64 only; place manifoldc alongside the assembly, " +
        "or use BspGeometryEngine.CreateManagedBsp() for the pure managed kernel.");

    public static Error Unusable(Exception exception) => new(
        "Manifold.Unavailable",
        $"The native Manifold library could not be loaded: {exception.Message}");

    /// <summary>
    /// The library is present but does not match this binding. Deliberately not one of the
    /// errors that falls back to the managed kernel: a wrong export name or an
    /// unmarshallable signature is a defect here, and substituting another kernel would
    /// turn it into a silent loss of the watertightness guarantee.
    /// </summary>
    public static Error BindingMismatch(Exception exception) => new(
        "Manifold.BindingMismatch",
        "The native Manifold library does not match this binding - it is likely a different " +
        $"version than the one expected (Manifold 3.x): {exception.Message}. " +
        "Use BspGeometryEngine.CreateManagedBsp() to run without it.");

    public static Error EmptyOperand(string name) => new(
        "Manifold.EmptyOperand",
        $"Mesh '{name}' has no geometry, so it cannot be used as a boolean operand.");

    public static Error InvalidMesh(string name, ManifoldError status) => new(
        "Manifold.InvalidMesh",
        $"Mesh '{name}' is not a valid manifold: {status}");

    public static Error OperationFailed(ManifoldError status) => new(
        "Manifold.OperationFailed",
        $"Boolean operation failed with status: {status}");

    public static readonly Error EmptyResult =
        new("Manifold.EmptyResult", "The operation produced an empty solid.");

    public static Error UnexpectedLayout(string detail) => new(
        "Manifold.UnexpectedLayout",
        $"The native library returned a mesh in an unexpected layout: {detail}.");
}
