using System.Runtime.InteropServices;

namespace GeometryEngine.Internal.Native;

internal enum ManifoldError
{
    NoError = 0,
    NonFiniteVertex = 1,
    NotManifold = 2,
    VertexIndexOutOfBounds = 3,
    PropertiesWrongLength = 4,
    MissingPositionProperties = 5,
    MergeVectorsDifferentLengths = 6,
    MergeIndexOutOfBounds = 7,
    TransformWrongLength = 8,
    RunIndexWrongLength = 9,
    FaceIdWrongLength = 10,
    InvalidConstruction = 11,
    ResultTooLarge = 12,
    InvalidTangents = 13,
    Cancelled = 14,
}

internal static unsafe class ManifoldNative
{
    private const string LibraryName = "manifoldc";

    /// <summary>
    /// Whether the native library could be located and loaded in this process.
    ///
    /// Asked <em>before</em> any P/Invoke, so that a host without the native binaries gets
    /// a described failure instead of a <see cref="DllNotFoundException"/> escaping from the
    /// middle of an operation. Only prebuilt win-x64 binaries ship with the library today,
    /// so on every other platform this is false and the caller must have a fallback.
    /// Evaluated once: whether the library is present cannot change during a process.
    /// </summary>
    public static bool IsAvailable => AvailableLazy.Value;

    private static readonly Lazy<bool> AvailableLazy = new(
        () => NativeLibraryResolver.ProbePaths(LibraryName).Any(File.Exists),
        LazyThreadSafetyMode.ExecutionAndPublication);

    static ManifoldNative()
    {
        NativeLibraryResolver.Register(LibraryName);
    }

    /// <summary>Signed distance callback for the level-set mesher. Manifold keeps where it is positive.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double SdfCallback(double x, double y, double z, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeVec2
    {
        public double X;
        public double Y;
    }

    // Allocation and destruction
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_alloc_manifold();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void manifold_delete_manifold(IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_alloc_meshgl64();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void manifold_delete_meshgl64(IntPtr m);

    // MeshGL64 construction
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_meshgl64(
        IntPtr mem,
        double* vertProps,
        nuint nVerts,
        nuint nProps,
        ulong* triVerts,
        nuint nTris);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_of_meshgl64(IntPtr mem, IntPtr mesh);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_meshgl64_merge(IntPtr mem, IntPtr mesh);

    // Boolean operations
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_union(IntPtr mem, IntPtr a, IntPtr b);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_difference(IntPtr mem, IntPtr a, IntPtr b);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_intersection(IntPtr mem, IntPtr a, IntPtr b);

    // Diagnostics / info
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int manifold_is_empty(IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ManifoldError manifold_status(IntPtr m);

    // Mesh extraction
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_get_meshgl64(IntPtr mem, IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint manifold_meshgl64_num_vert(IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint manifold_meshgl64_num_tri(IntPtr m);

    /// <summary>
    /// How many properties each vertex carries. At least three - the position - but a mesh
    /// may carry more, so this is the stride through the interleaved property buffer.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint manifold_meshgl64_num_prop(IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint manifold_meshgl64_vert_properties_length(IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint manifold_meshgl64_tri_length(IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double* manifold_meshgl64_vert_properties(IntPtr mem, IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong* manifold_meshgl64_tri_verts(IntPtr mem, IntPtr m);

    /// <summary>
    /// How many vertex pairs in a <c>MeshGL64</c> are one position split in two. Manifold's
    /// mesh output keys vertices by their full property set, and splits a position wherever runs
    /// from different operands meet there; these pairs are what stitch it back together.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint manifold_meshgl64_merge_length(IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong* manifold_meshgl64_merge_from_vert(IntPtr mem, IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong* manifold_meshgl64_merge_to_vert(IntPtr mem, IntPtr m);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint manifold_num_tri(IntPtr m);

    // Level set. Signatures checked against bindings/c/include/manifold/manifoldc.h at the
    // commit the shipped binaries were built from; see runtimes/README.md.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_alloc_box();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void manifold_delete_box(IntPtr b);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_box(IntPtr mem, double x1, double y1, double z1, double x2, double y2, double z2);

    /// <summary>
    /// Meshes the isosurface where the field equals <c>level</c>. Manifold keeps the region where
    /// the field is greater, so the field must read positive inside the solid.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_level_set(
        IntPtr mem, SdfCallback sdf, IntPtr bounds, double edgeLength, double level, double tolerance, IntPtr context);

    // Extrusion
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_alloc_simple_polygon();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void manifold_delete_simple_polygon(IntPtr p);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_alloc_polygons();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void manifold_delete_polygons(IntPtr p);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_simple_polygon(IntPtr mem, NativeVec2* points, nuint length);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_polygons(IntPtr mem, IntPtr* simplePolygons, nuint length);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_extrude(
        IntPtr mem, IntPtr polygons, double height, int slices, double twistDegrees, double scaleX, double scaleY);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_translate(IntPtr mem, IntPtr m, double x, double y, double z);
}
