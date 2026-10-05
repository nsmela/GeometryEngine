namespace GeometryEngine.Internal.Native;

/// <summary>A native solid kept with the mesh it was read in from. Not yet kept: see the tests.</summary>
internal sealed class RetainedSolid
{
    /// <summary>How many are alive in the process. For tests and leak checks.</summary>
    public static long Live => 0;
}
