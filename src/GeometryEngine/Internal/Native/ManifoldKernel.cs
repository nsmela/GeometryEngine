using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GeometryEngine.Core.Common;
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

            fixed (double* pOutVerts = outVertProps)
            fixed (ulong* pOutTris = outTriVerts)
            {
                ManifoldNative.manifold_meshgl64_vert_properties((IntPtr)pOutVerts, meshGl);
                ManifoldNative.manifold_meshgl64_tri_verts((IntPtr)pOutTris, meshGl);
            }

            var vertices = ImmutableArray.CreateBuilder<Vec3>(nVerts);
            for (var i = 0; i < nVerts; i++)
            {
                vertices.Add(new Vec3(
                    outVertProps[i * stride],
                    outVertProps[(i * stride) + 1],
                    outVertProps[(i * stride) + 2]));
            }

            var triangles = ImmutableArray.CreateBuilder<int>(triVertsLen);
            for (var i = 0; i < triVertsLen; i++)
            {
                triangles.Add((int)outTriVerts[i]);
            }

            return ImmutableMesh.Create(vertices.MoveToImmutable(), triangles.MoveToImmutable(), metadata);
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

    public static Error UnexpectedLayout(string detail) => new(
        "Manifold.UnexpectedLayout",
        $"The native library returned a mesh in an unexpected layout: {detail}.");
}
