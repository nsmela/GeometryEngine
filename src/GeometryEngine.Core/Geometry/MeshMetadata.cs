namespace GeometryEngine.Core.Geometry;

/// <summary>
/// Descriptive data travelling with a mesh. It is a record, so a caller changes it
/// by producing a new value with <c>with</c>, never by assigning into an existing one.
/// </summary>
public sealed record MeshMetadata(string Name, string CreatedBy)
{
    public static readonly MeshMetadata Anonymous = new("mesh", "unknown");

    public static MeshMetadata Named(string name) => Anonymous with { Name = name };

    public MeshMetadata WithName(string name) => this with { Name = name };

    public MeshMetadata WithCreatedBy(string createdBy) => this with { CreatedBy = createdBy };
}
