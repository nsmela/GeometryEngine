using System.Collections.Immutable;

namespace GeometryEngine.Core.Geometry;

/// <summary>
/// An indexed triangle mesh. Deliberately narrower than the Fabolus original:
/// vertices and triangles are exposed as immutable arrays rather than <c>Vector3[]</c>
/// and <c>int[]</c>, so handing a mesh to a caller cannot let them corrupt it.
/// </summary>
public interface IMesh
{
    /// <summary>Vertex positions, indexed by <see cref="Triangles"/>.</summary>
    ImmutableArray<Vec3> Vertices { get; }

    /// <summary>Triangle corner indices, three per triangle, wound counter-clockwise when seen from outside.</summary>
    ImmutableArray<int> Triangles { get; }

    MeshMetadata Metadata { get; }

    int VertexCount { get; }

    int TriangleCount { get; }

    bool IsEmpty { get; }

    /// <summary>Returns a mesh with the same geometry and different metadata.</summary>
    IMesh WithMetadata(MeshMetadata metadata);

    /// <summary>The three corner positions of one triangle.</summary>
    (Vec3 A, Vec3 B, Vec3 C) TriangleAt(int triangleIndex);
}

/// <summary>
/// The only implementation of <see cref="IMesh"/> in the library. It cannot be
/// constructed in an invalid state: <see cref="Create"/> rejects ragged index
/// arrays, out-of-range indices and non-finite coordinates.
/// </summary>
public sealed class ImmutableMesh : IMesh
{
    public static readonly ImmutableMesh Empty =
        new(ImmutableArray<Vec3>.Empty, ImmutableArray<int>.Empty, MeshMetadata.Named("empty"));

    public ImmutableArray<Vec3> Vertices { get; }
    public ImmutableArray<int> Triangles { get; }
    public MeshMetadata Metadata { get; }

    public int VertexCount => Vertices.Length;
    public int TriangleCount => Triangles.Length / 3;
    public bool IsEmpty => Triangles.IsEmpty;

    private ImmutableMesh(ImmutableArray<Vec3> vertices, ImmutableArray<int> triangles, MeshMetadata metadata)
    {
        Vertices = vertices;
        Triangles = triangles;
        Metadata = metadata;
    }

    public static Result<IMesh> Create(
        ImmutableArray<Vec3> vertices,
        ImmutableArray<int> triangles,
        MeshMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (triangles.Length % 3 != 0)
        {
            return MeshErrors.RaggedTriangleArray(triangles.Length);
        }

        for (var i = 0; i < triangles.Length; i++)
        {
            if (triangles[i] < 0 || triangles[i] >= vertices.Length)
            {
                return MeshErrors.IndexOutOfRange(triangles[i], vertices.Length);
            }
        }

        for (var i = 0; i < vertices.Length; i++)
        {
            if (!vertices[i].IsFinite)
            {
                return MeshErrors.NonFiniteVertex(i);
            }
        }

        return Result.Success<IMesh>(new ImmutableMesh(vertices, triangles, metadata));
    }

    public IMesh WithMetadata(MeshMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new ImmutableMesh(Vertices, Triangles, metadata);
    }

    public (Vec3 A, Vec3 B, Vec3 C) TriangleAt(int triangleIndex)
    {
        if (triangleIndex < 0 || triangleIndex >= TriangleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(triangleIndex));
        }

        var offset = triangleIndex * 3;
        return (Vertices[Triangles[offset]], Vertices[Triangles[offset + 1]], Vertices[Triangles[offset + 2]]);
    }

    public override string ToString() => $"{Metadata.Name} ({VertexCount} verts, {TriangleCount} tris)";
}

/// <summary>Errors raised while building a mesh.</summary>
public static class MeshErrors
{
    public static Error RaggedTriangleArray(int length) =>
        new("Mesh.RaggedTriangleArray", $"Triangle index count {length} is not a multiple of three.");

    public static Error IndexOutOfRange(int index, int vertexCount) =>
        new("Mesh.IndexOutOfRange", $"Triangle index {index} does not address any of the {vertexCount} vertices.");

    public static Error NonFiniteVertex(int index) =>
        new("Mesh.NonFiniteVertex", $"Vertex {index} is NaN or infinite.");

    public static readonly Error EmptyOperand =
        new("Mesh.EmptyOperand", "The operation needs a mesh with at least one triangle.");
}
