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

/// <summary>Manifold's <c>ManifoldOpType</c>, in its declaration order.</summary>
internal enum ManifoldOpType
{
    Add = 0,
    Subtract = 1,
    Intersect = 2,
}

/// <summary>
/// Manifold's <c>ManifoldManifoldPair</c>: two manifolds returned together, each constructed in
/// the memory the caller passed for it. Returned by value, which the default marshaller handles
/// for a blittable struct.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ManifoldPair
{
    public IntPtr First;
    public IntPtr Second;
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

    /// <summary>
    /// The solid scaled about the origin. Like every transform here it is recorded, not applied:
    /// Manifold multiplies it into whatever transform the solid already carries and moves the
    /// vertices once, when the solid is next needed.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_scale(IntPtr mem, IntPtr m, double x, double y, double z);

    /// <summary>
    /// The solid under an affine map given by its columns: where the x, y and z axes go, then
    /// the translation.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_transform(
        IntPtr mem,
        IntPtr m,
        double x1,
        double y1,
        double z1,
        double x2,
        double y2,
        double z2,
        double x3,
        double y3,
        double z3,
        double x4,
        double y4,
        double z4);

    /// <summary>
    /// A second handle to the same solid. Manifold's solids are immutable and shared by
    /// reference, so this copies a pointer, not geometry.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_copy(IntPtr mem, IntPtr m);

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

    // Batch booleans. The vector holds copies of the manifolds pushed into it - a Manifold is a
    // shared handle onto its geometry, so a copy is cheap - and the caller still owns, and must
    // delete, each manifold it pushed. Signatures and enum order checked against
    // bindings/c/include/manifold/manifoldc.h and types.h of the shipped version.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_alloc_manifold_vec();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void manifold_delete_manifold_vec(IntPtr ms);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_manifold_empty_vec(IntPtr mem);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void manifold_manifold_vec_push_back(IntPtr ms, IntPtr m);

    /// <summary>
    /// Combines every manifold in the vector at once. For <see cref="ManifoldOpType.Subtract"/>
    /// the first is the subject and every later one is taken away from it.
    /// </summary>
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_batch_boolean(IntPtr mem, IntPtr ms, ManifoldOpType op);

    // Plane cuts. The plane is normal . p = offset; the normal need not be unit length, as
    // Manifold normalises it, but the offset is then measured along the unit normal.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ManifoldPair manifold_split_by_plane(
        IntPtr memFirst, IntPtr memSecond, IntPtr m, double normalX, double normalY, double normalZ, double offset);

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

    // Simplification. Collapses edges whose removal moves no surface by more than tolerance,
    // and keeps a subset of the original vertices - it never invents positions the way a
    // quadric decimator does. Tolerance 0 means "use the manifold's own", and a value below
    // that is raised to it, so how far it reduces is decided by the geometry and not by the
    // caller. Not a triangle-count target; see DecimateCompare in the benchmarks.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_simplify(IntPtr mem, IntPtr m, double tolerance);

    // Tangent-based smoothing, in the order it has to be called. CalculateNormals writes vertex
    // normals into property channels, sharing them across every edge it does not consider sharp;
    // SmoothByNormals turns those into halfedge tangents; and only Refine* moves any geometry, by
    // interpolating the surface through them. Rounding a crease and leaving a plane alone both
    // fall out of that: shared normals bend the patch across a crease, while a patch through
    // coplanar vertices with in-plane tangents is itself planar.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_calculate_normals(IntPtr mem, IntPtr m, int normalIdx, double minSharpAngle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_smooth_by_normals(IntPtr mem, IntPtr m, int normalIdx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr manifold_refine_to_tolerance(IntPtr mem, IntPtr m, double tolerance);

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
