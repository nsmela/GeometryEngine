namespace GeometryEngine.Core.Geometry;

/// <summary>
/// What an operation did to a mesh's geometry, which is all a consumer needs to know to decide
/// what it had cached about that mesh is still true. The distinctions are drawn where the answer
/// differs, not where the implementation does: every modifier rebuilds a surface, so they share
/// one member, while a transform is separate because it moves a mesh without re-meshing it.
/// </summary>
public enum MeshOperation
{
    /// <summary>
    /// Vertices moved, connectivity untouched: a translation, scale or rotation, or a projection
    /// that drapes a mesh onto a surface. Anything describing how the mesh is put together still
    /// holds; anything measuring where it is or how big it is does not.
    /// </summary>
    Transform,

    /// <summary>
    /// The surface was rebuilt: offset, decimated, smoothed, repaired or split into components.
    /// Vertex and triangle counts may differ, and so may the volume.
    /// </summary>
    Rebuild,

    /// <summary>
    /// Two meshes were combined into one, as a boolean or a projection does. The result is not
    /// either operand, so anything either of them carried describes something that no longer exists.
    /// </summary>
    Combine,
}

/// <summary>
/// Consumer data travelling with a mesh. The engine never reads it and never constructs one - it
/// only carries the value from a mesh to the mesh derived from it, asking <see cref="Carry"/> what
/// should survive on the way.
///
/// This is deliberately one slot rather than a property bag: the consumer keeps its own data in a
/// type it declares, so nothing here is stringly-typed or needs casting on the way out. The cost
/// is that the slot has one occupant, which is the right trade for a library with one consumer.
/// </summary>
public interface IMeshAnnotations
{
    /// <summary>
    /// The annotations that remain true of the mesh <paramref name="operation"/> produced, or null
    /// when none of them do. The engine cannot answer this - only the consumer knows what its own
    /// values mean - so it asks rather than guessing, and a consumer that wants nothing carried
    /// returns null for everything.
    /// </summary>
    IMeshAnnotations? Carry(MeshOperation operation);
}

/// <summary>
/// Descriptive data travelling with a mesh. It is a record, so a caller changes it
/// by producing a new value with <c>with</c>, never by assigning into an existing one.
/// </summary>
/// <remarks>
/// Left open rather than sealed only so that a consumer still carrying its own subclass keeps
/// building. Nothing here is designed to be derived from - <see cref="Annotations"/> is how a
/// consumer attaches its own data - so this can be sealed again once no checkout subclasses it.
/// </remarks>
public record MeshMetadata(string Name, string CreatedBy)
{
    public static readonly MeshMetadata Anonymous = new("mesh", "unknown");

    /// <summary>
    /// Consumer data, carried by operations that derive one mesh from another and absent on a
    /// mesh the engine built from nothing. See <see cref="IMeshAnnotations"/>.
    /// </summary>
    public IMeshAnnotations? Annotations { get; init; }

    public static MeshMetadata Named(string name) => Anonymous with { Name = name };

    public MeshMetadata WithName(string name) => this with { Name = name };

    public MeshMetadata WithCreatedBy(string createdBy) => this with { CreatedBy = createdBy };

    public MeshMetadata WithAnnotations(IMeshAnnotations? annotations) => this with { Annotations = annotations };

    /// <summary>
    /// This metadata as it should appear on a mesh derived from the one carrying it: the new
    /// producer recorded, and the annotations narrowed to whatever survives
    /// <paramref name="operation"/>.
    ///
    /// Every operation that derives a mesh from another goes through here rather than writing
    /// <c>metadata with { CreatedBy = ... }</c> directly, so that carrying the annotations is not
    /// something each one has to remember - forgetting it was how a boolean used to silently drop
    /// everything its operands carried.
    /// </summary>
    /// <param name="createdBy">The producer to record, or null to keep the one already there.</param>
    public MeshMetadata CarriedThrough(MeshOperation operation, string? createdBy = null) =>
        this with { CreatedBy = createdBy ?? CreatedBy, Annotations = Annotations?.Carry(operation) };
}
