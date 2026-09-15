using System.Reflection;
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
        () => ProbePaths().Any(path => File.Exists(path)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    static ManifoldNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(ManifoldNative).Assembly, DllImportResolver);
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            return IntPtr.Zero;
        }

        foreach (var path in ProbePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Every location the native library might sit in, most specific first.
    ///
    /// Both the assembly's own directory and the host application's base directory are
    /// searched, and they are not the same thing: when this library is deployed into a
    /// plug-in subfolder the natives travel with the assembly, not with the host, so
    /// probing only <see cref="AppContext.BaseDirectory"/> would miss them.
    /// </summary>
    private static IEnumerable<string> ProbePaths()
    {
        foreach (var root in Roots())
        {
            // Beside the assembly, as the build copies them.
            yield return Path.Combine(root, "manifoldc.dll");
            yield return Path.Combine(root, "libmanifoldc.so");
            yield return Path.Combine(root, "libmanifoldc.dylib");

            // Or in the NuGet runtimes layout, for a packaged consumer.
            foreach (var rid in new[] { "win-x64", "win-arm64" })
            {
                yield return Path.Combine(root, "runtimes", rid, "native", "manifoldc.dll");
            }

            foreach (var rid in new[] { "linux-x64", "linux-arm64" })
            {
                yield return Path.Combine(root, "runtimes", rid, "native", "libmanifoldc.so");
            }

            foreach (var rid in new[] { "osx-arm64", "osx-x64" })
            {
                yield return Path.Combine(root, "runtimes", rid, "native", "libmanifoldc.dylib");
            }
        }
    }

    private static IEnumerable<string> Roots()
    {
        // Assembly.Location is empty in a single-file publish, hence the guard.
        var assemblyDirectory = Path.GetDirectoryName(typeof(ManifoldNative).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return assemblyDirectory;
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDirectory) &&
            !string.Equals(baseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                assemblyDirectory?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            yield return baseDirectory;
        }
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
}
