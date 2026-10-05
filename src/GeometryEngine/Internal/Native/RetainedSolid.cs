using System.Runtime.InteropServices;

namespace GeometryEngine.Internal.Native;

/// <summary>
/// A native solid kept with the mesh it stands for, so the kernel need not read that mesh in
/// again: the halfedges, the sort and the collision tree it built the first time are all here.
///
/// It is never handed to an operation. An operation takes a <see cref="Copy"/>, which in
/// Manifold is a second reference to the same immutable solid and costs nothing to make, and
/// frees that copy as it frees every handle it owns. So nothing outside this class can free what
/// is kept, and what is kept cannot be freed under an operation still using it.
///
/// It lives exactly as long as its mesh: the mesh's measurements hold the only reference, and
/// the finalizer releases the native memory once they are collected. That memory is invisible to
/// the collector, which would otherwise see a few dozen bytes where there are megabytes, so its
/// size is declared as pressure for as long as it is held.
/// </summary>
/// <remarks>
/// Safe to copy from several threads at once. The handle is only ever read, and it always holds
/// an evaluated solid - one read in, or one whose status has been read - which Manifold guards
/// with a mutex where it is shared and otherwise never changes.
/// </remarks>
internal sealed class RetainedSolid : SafeHandle
{
    /// <summary>
    /// What one triangle of a solid costs the kernel to hold, measured at 12.8k and 100k
    /// triangles on Manifold 7c86359. The mesh itself holds about 24.
    /// </summary>
    public const long BytesPerTriangle = 212;

    private static long _live;

    private readonly long _pressure;

    /// <param name="owned">A handle this takes ownership of. The caller must not free it.</param>
    /// <param name="merged">Whether the mesh had to be welded before the kernel would take it.</param>
    public RetainedSolid(IntPtr owned, bool merged, int triangleCount)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(owned);
        Merged = merged;
        _pressure = Math.Max(1, triangleCount) * BytesPerTriangle;
        GC.AddMemoryPressure(_pressure);
        Interlocked.Increment(ref _live);
    }

    /// <summary>How many are alive in the process. For tests and leak checks.</summary>
    public static long Live => Interlocked.Read(ref _live);

    public bool Merged { get; }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>A handle to the same solid, owned by the caller and freed like any other.</summary>
    public IntPtr Copy()
    {
        var held = false;
        try
        {
            DangerousAddRef(ref held);
            return ManifoldNative.manifold_copy(ManifoldNative.manifold_alloc_manifold(), handle);
        }
        finally
        {
            if (held)
            {
                DangerousRelease();
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        ManifoldNative.manifold_delete_manifold(handle);
        GC.RemoveMemoryPressure(_pressure);
        Interlocked.Decrement(ref _live);
        return true;
    }
}
