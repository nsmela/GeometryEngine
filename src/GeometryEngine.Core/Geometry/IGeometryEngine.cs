using System.Collections.Immutable;

namespace GeometryEngine.Core.Geometry;

/// <summary>
/// Factory and operation surface for geometry, modelled on the Fabolus v1
/// <c>IGeometryEngine</c>. Implementations may wrap a native kernel or, as here,
/// be pure managed code. Only the sub-interfaces this library needs are present.
/// </summary>
public interface IGeometryEngine
{
    /// <summary>Boolean operations: union, subtract, intersect.</summary>
    IBooleans Booleans { get; }

    /// <summary>Procedural mesh generation: boxes, spheres, cylinders.</summary>
    IGeometryGenerators Generators { get; }

    /// <summary>Measurement and topology inspection.</summary>
    IGeometryEvaluators Evaluators { get; }

    /// <summary>Rigid and affine transformations.</summary>
    IGeometryTransforms Transforms { get; }

    /// <summary>Import and export of mesh files.</summary>
    IGeometryIO IO { get; }

    /// <summary>Creates a mesh from raw vertex and triangle data, validating it.</summary>
    Result<IMesh> CreateMesh(ImmutableArray<Vec3> vertices, ImmutableArray<int> triangles, MeshMetadata metadata);
}

/// <summary>Boolean (CSG) operations on closed meshes.</summary>
public interface IBooleans
{
    /// <summary>Everything inside A or inside B.</summary>
    Result<IMesh> Union(IMesh meshA, IMesh meshB);

    /// <summary>Everything inside A that is not inside B.</summary>
    Result<IMesh> Subtract(IMesh meshA, IMesh meshB);

    /// <summary>Everything inside both A and B.</summary>
    Result<IMesh> Intersect(IMesh meshA, IMesh meshB);
}

/// <summary>Procedural mesh generation.</summary>
public interface IGeometryGenerators
{
    /// <summary>An axis-aligned box spanning the two corner points.</summary>
    Result<IMesh> GenerateBox(Vec3 min, Vec3 max);

    /// <summary>A UV sphere. <paramref name="segments"/> controls both rings and slices.</summary>
    Result<IMesh> GenerateSphere(Vec3 centre, double radius, int segments = 16);

    /// <summary>A capped cylinder aligned to the Z axis.</summary>
    Result<IMesh> GenerateCylinder(Vec3 baseCentre, double radius, double height, int segments = 24);
}

/// <summary>Aggregate measurements of a mesh.</summary>
public sealed record MeshStatistics(
    double Volume,
    double SurfaceArea,
    Vec3 BoundsMin,
    Vec3 BoundsMax,
    int VertexCount,
    int TriangleCount)
{
    public Vec3 BoundsSize => BoundsMax - BoundsMin;
}

/// <summary>The result of inspecting mesh topology.</summary>
public sealed record TopologyValidation(
    int BoundaryEdgeCount,
    int NonManifoldEdgeCount,
    int DegenerateTriangleCount,
    int DuplicateVertexCount,
    int InconsistentWindingEdgeCount,
    int DuplicateFaceCount,
    int ShellCount)
{
    /// <summary>
    /// No holes: every edge is used by at least two triangles, so the surface encloses a
    /// volume. This is the defect that decides printability - a mesh with a hole has no
    /// well-defined inside, and a slicer cannot fill it.
    /// </summary>
    public bool IsClosed => BoundaryEdgeCount == 0;

    /// <summary>
    /// Every edge is used by at most two triangles, so no two sheets of surface meet along
    /// one edge and no face is doubled. Less serious than a hole: such a mesh still encloses
    /// a volume, and a doubled face of opposite winding encloses none, so it can usually be
    /// cleaned away rather than repaired.
    /// </summary>
    public bool IsEdgeManifold => NonManifoldEdgeCount == 0;

    /// <summary>
    /// A mesh is watertight when every edge is shared by exactly two triangles - closed
    /// <em>and</em> edge-manifold.
    ///
    /// Note that this is deliberately stricter than what a mesh kernel typically demands.
    /// Manifold, for instance, requires only that every <em>directed</em> half-edge have
    /// exactly one opposite partner, which a pair of coincident faces of opposite winding
    /// satisfies: the edge then carries four faces that pair up cleanly. Such a mesh fails
    /// <see cref="IsEdgeManifold"/> here while Manifold accepts it - two defensible
    /// definitions, not a disagreement about the facts. Where the distinction matters, ask
    /// <see cref="IsClosed"/> and <see cref="IsEdgeManifold"/> separately: only the first
    /// reports a hole, and only a hole makes a mesh unprintable.
    /// </summary>
    public bool IsWatertight => IsClosed && IsEdgeManifold;

    /// <summary>
    /// Every pair of adjacent faces traverses their shared edge in opposite directions, as a
    /// consistently oriented surface must.
    ///
    /// Read this alongside <see cref="IsEdgeManifold"/> rather than on its own. A doubled face
    /// generally breaks both, because the duplicate re-traverses half-edges its twin already
    /// used - so a non-zero count here is at least as often a *symptom* of a redundant face as
    /// it is evidence of a genuinely inverted one. Chasing it as an orientation bug when
    /// <see cref="IsEdgeManifold"/> is also false is usually chasing the wrong defect.
    /// </summary>
    public bool IsConsistentlyWound => InconsistentWindingEdgeCount == 0;

    /// <summary>
    /// The mesh carries elements that describe no surface and that a cleaner could remove
    /// without changing the solid: zero-area triangles, coincident vertices, or repeated faces.
    /// Untidy rather than broken - a mesh can be a perfectly good solid and still be redundant.
    /// </summary>
    public bool HasRedundantGeometry =>
        DegenerateTriangleCount > 0 || DuplicateVertexCount > 0 || DuplicateFaceCount > 0;

    /// <summary>
    /// True when the mesh is watertight, consistently wound, and free of slivers,
    /// duplicate vertices and duplicate faces. Shell count is not part of this: more
    /// than one shell can be perfectly valid.
    ///
    /// This is the conjunction of four independent questions, and a caller that only needs one
    /// of them should ask it directly - <see cref="IsClosed"/>, <see cref="IsEdgeManifold"/>,
    /// <see cref="IsConsistentlyWound"/>, <see cref="HasRedundantGeometry"/>. They are not
    /// comparable in severity: only an unclosed mesh has no well-defined interior, and only that
    /// makes it unprintable. The rest describe surfaces that enclose a solid correctly while
    /// carrying something redundant.
    /// </summary>
    public bool IsClean => IsWatertight && IsConsistentlyWound && !HasRedundantGeometry;
}

/// <summary>Measurement and inspection of meshes.</summary>
public interface IGeometryEvaluators
{
    Result<MeshStatistics> GetStatistics(IMesh mesh);

    Result<TopologyValidation> ValidateTopology(IMesh mesh);

    /// <summary>Splits a mesh into its connected components; a single component returns one mesh.</summary>
    Result<ImmutableArray<IMesh>> SeparateComponents(IMesh mesh);
}

/// <summary>Transformations. Every method returns a new mesh.</summary>
public interface IGeometryTransforms
{
    Result<IMesh> Translate(IMesh mesh, Vec3 offset);

    Result<IMesh> Scale(IMesh mesh, Vec3 factors);

    Result<IMesh> Rotate(IMesh mesh, Direction axis, double radians);
}

/// <summary>Mesh file import and export.</summary>
public interface IGeometryIO
{
    Result Export(IMesh mesh, string filePath, bool overwrite = false);

    Result<IMesh> Import(string filePath);
}
