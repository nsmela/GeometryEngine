using System.Reflection;
using System.Runtime.InteropServices;

namespace GeometryEngine.Internal.Native;

/// <summary>
/// The assembly's one <see cref="NativeLibrary"/> resolver, shared by every native binding here.
///
/// <see cref="NativeLibrary.SetDllImportResolver"/> permits a single resolver per assembly and
/// throws on a second registration. Two bindings each registering their own would work only
/// until both were loaded in one process, at which point whichever static constructor ran
/// second would die with a TypeInitializationException - and a failed static constructor is
/// permanent, so that binding would stay broken for the life of the process.
/// </summary>
internal static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<IEnumerable<string>>> Candidates = new(StringComparer.Ordinal);
    private static bool _registered;

    /// <summary>
    /// Registers the paths to try for one library name. Safe to call from several static
    /// constructors; the resolver itself is installed once, on the first call.
    /// </summary>
    public static void Register(string libraryName)
    {
        lock (Gate)
        {
            Candidates[libraryName] = () => ProbePaths(libraryName);

            if (_registered)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
            _registered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        Func<IEnumerable<string>>? probePaths;
        lock (Gate)
        {
            if (!Candidates.TryGetValue(libraryName, out probePaths))
            {
                return IntPtr.Zero;
            }
        }

        foreach (var path in probePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Every location a native library might sit in, most specific first: beside the assembly
    /// under each platform's naming convention, then in the NuGet runtimes layout.
    ///
    /// Both the assembly's own directory and the host application's base directory are
    /// searched, and they are not the same thing: when this library is deployed into a plug-in
    /// subfolder the natives travel with the assembly, not with the host, so probing only
    /// <see cref="AppContext.BaseDirectory"/> would miss them.
    /// </summary>
    public static IEnumerable<string> ProbePaths(string stem)
    {
        foreach (var root in Roots())
        {
            yield return Path.Combine(root, $"{stem}.dll");
            yield return Path.Combine(root, $"lib{stem}.so");
            yield return Path.Combine(root, $"{stem}.so");
            yield return Path.Combine(root, $"lib{stem}.dylib");

            foreach (var rid in new[] { "win-x64", "win-arm64" })
            {
                yield return Path.Combine(root, "runtimes", rid, "native", $"{stem}.dll");
            }

            foreach (var rid in new[] { "linux-x64", "linux-arm64" })
            {
                yield return Path.Combine(root, "runtimes", rid, "native", $"lib{stem}.so");
                yield return Path.Combine(root, "runtimes", rid, "native", $"{stem}.so");
            }

            foreach (var rid in new[] { "osx-arm64", "osx-x64" })
            {
                yield return Path.Combine(root, "runtimes", rid, "native", $"lib{stem}.dylib");
            }
        }
    }

    private static IEnumerable<string> Roots()
    {
        // Assembly.Location is empty in a single-file publish, hence the guard.
        var assemblyDirectory = Path.GetDirectoryName(typeof(NativeLibraryResolver).Assembly.Location);
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
}
