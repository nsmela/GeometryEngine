namespace GeometryEngine;

/// <summary>
/// Whether the native kernel keeps the solids it has read in and built, with the meshes they
/// belong to, so that a mesh used again is not read in again.
/// </summary>
/// <remarks>
/// Reading a mesh into the kernel costs about as much as a boolean on it. Keeping it took
/// 43-46% off repeated cuts of one body, and off a chain of calls each fed the last one's result,
/// in <c>bench retain</c>. The price is memory outside the managed heap: about 212 bytes a
/// triangle, some nine times what the mesh itself holds, for as long as the mesh is reachable.
/// It is released when the mesh is collected.
/// </remarks>
public enum SolidRetention
{
    /// <summary>Every operation reads its meshes in and lets them go when it returns.</summary>
    None,

    /// <summary>
    /// A mesh an operation reads in, and the mesh it produces, each keep their native solid for
    /// as long as they live. Copies made with <c>WithMetadata</c> share it; a moved mesh does not.
    /// </summary>
    Keep,
}
