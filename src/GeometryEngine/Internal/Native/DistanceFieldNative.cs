using System.Runtime.InteropServices;

namespace GeometryEngine.Internal.Native;

/// <summary>Status codes from the distance-field library. Mirrors GeStatus.</summary>
internal enum DistanceFieldStatus
{
    Ok = 0,
    InvalidArgument = -1,
    EmptyMesh = -2,
    ManifoldUnavailable = -3,
    LevelSetFailed = -4,
    Exception = -5,
}

/// <summary>A mesh owned by the native library. Mirrors GeMesh; released with ge_mesh_free.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMesh
{
    public IntPtr Vertices;
    public nuint VertexCount;
    public IntPtr Triangles;
    public nuint TriangleCount;
}

/// <summary>
/// P/Invoke surface for geometryengine_native, the libigl-backed distance field in native/.
///
/// It exists because Manifold's level-set mesher asks for a signed distance one point at a
/// time. Answering those from managed code costs a P/Invoke transition per sample, and a bolus
/// offset is a few hundred thousand samples - the transitions, not the field, are the expense.
/// So a whole offset is handed over in one call, and batch queries answer every point at once.
///
/// It is optional. Everything it does has a managed implementation behind the managed BVH, and
/// <see cref="IsAvailable"/> is false when the binary is absent, so a build without it is slower
/// rather than broken.
/// </summary>
internal static class DistanceFieldNative
{
    private const string LibraryName = "geometryengine_native";

    /// <summary>
    /// Must match GEOMETRYENGINE_NATIVE_ABI_VERSION. A library that disagrees is treated as
    /// absent: it is a stale binary beside a newer assembly, and calling into it would marshal
    /// against the wrong layout.
    /// </summary>
    private const int ExpectedAbiVersion = 1;

    static DistanceFieldNative()
    {
        NativeLibraryResolver.Register(LibraryName);
    }

    /// <summary>Whether the library is present, loadable, and built against the ABI this expects.</summary>
    public static bool IsAvailable => AvailableLazy.Value;

    private static readonly Lazy<bool> AvailableLazy = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool Probe()
    {
        if (!NativeLibraryResolver.ProbePaths(LibraryName).Any(File.Exists))
        {
            return false;
        }

        try
        {
            return ge_abi_version() == ExpectedAbiVersion;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ge_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ge_mesh_free(ref NativeMesh mesh);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ge_field_create(
        [In] double[] vertices, nuint vertexCount, [In] int[] triangles, nuint triangleCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ge_field_destroy(IntPtr field);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ge_field_signed_distance(
        DistanceFieldHandle field, [In] double[] points, nuint count, [Out] double[] results);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ge_field_closest_point(
        DistanceFieldHandle field,
        [In] double[] points,
        nuint count,
        [Out] double[]? closestPoints,
        [Out] int[]? triangles,
        [Out] double[]? distances);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ge_field_raycast(
        DistanceFieldHandle field,
        [In] double[] origins,
        [In] double[] directions,
        nuint count,
        [Out] double[] distances,
        [Out] int[] triangles);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ge_offset(
        [In] double[] vertices,
        nuint vertexCount,
        [In] int[] triangles,
        nuint triangleCount,
        double offsetDistance,
        [In] double[] bounds,
        double edgeLength,
        out NativeMesh result);
}

/// <summary>
/// Owns a native field. A <see cref="SafeHandle"/> rather than a bare pointer, so a field whose
/// owner is never disposed is still freed by the finalizer, and a query in flight keeps the
/// handle alive past a concurrent dispose.
/// </summary>
internal sealed class DistanceFieldHandle : SafeHandle
{
    public DistanceFieldHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public static DistanceFieldHandle? Create(IMesh mesh)
    {
        var raw = DistanceFieldNative.ge_field_create(
            NativeMeshArrays.Flatten(mesh.Vertices),
            (nuint)mesh.VertexCount,
            [.. mesh.Triangles],
            (nuint)mesh.TriangleCount);

        if (raw == IntPtr.Zero)
        {
            return null;
        }

        var owned = new DistanceFieldHandle();
        owned.SetHandle(raw);
        return owned;
    }

    protected override bool ReleaseHandle()
    {
        DistanceFieldNative.ge_field_destroy(handle);
        return true;
    }
}

/// <summary>Marshalling helpers shared by the native bindings.</summary>
internal static class NativeMeshArrays
{
    public static double[] Flatten(ImmutableArray<Vec3> points)
    {
        var flat = new double[points.Length * 3];
        for (var i = 0; i < points.Length; i++)
        {
            flat[i * 3] = points[i].X;
            flat[(i * 3) + 1] = points[i].Y;
            flat[(i * 3) + 2] = points[i].Z;
        }

        return flat;
    }
}
