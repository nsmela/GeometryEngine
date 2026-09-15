using System.Collections.Immutable;

namespace GeometryEngine.Core.Geometry;

/// <summary>
/// Factory and operation surface for geometry, modelled on the Fabolus v1
/// <c>IGeometryEngine</c> and complete enough to stand in for its MeshLib backend.
/// </summary>
public interface IGeometryEngine
{
    /// <summary>Boolean operations: union, subtract, intersect.</summary>
    IBooleans Booleans { get; }

    /// <summary>Procedural mesh generation: primitives, tubes, swept and draped paths.</summary>
    IGeometryGenerators Generators { get; }

    /// <summary>Measurement and topology inspection.</summary>
    IGeometryEvaluators Evaluators { get; }

    /// <summary>Rigid and affine transformations.</summary>
    IGeometryTransforms Transforms { get; }

    /// <summary>Import and export of mesh files and packages.</summary>
    IGeometryIO IO { get; }

    /// <summary>Operations that rebuild a surface: offsetting, decimation, repair.</summary>
    IGeometryModifiers Modifiers { get; }

    /// <summary>Ray, closest-point and signed-distance queries against a mesh.</summary>
    ISpatialQueries Spatial { get; }

    /// <summary>Planar polygon operations: outlines, offsets, unions, extrusion.</summary>
    IPolygonOperations Polygons { get; }

    /// <summary>Solids built from planar outlines and laid onto a curved surface.</summary>
    IDecalOperations Decals { get; }

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

/// <summary>A tube swept along a polyline, with its own radius at every path point.</summary>
public sealed record TubeSpec(ImmutableArray<Vec3> Path, ImmutableArray<double> Radii, int Segments = 16, bool Capped = true);

/// <summary>
/// A path buffered by <paramref name="Radius"/> in the XY plane and extruded vertically. The
/// bottom follows the path's own height - or, when a <paramref name="Surface"/> is given, the
/// surface directly below each point - sunk <paramref name="Depth"/> into it. The top is flat at
/// <paramref name="TopHeight"/>, but never less than one unit above where it rises from.
/// </summary>
public sealed record DrapedPathSpec(
    ImmutableArray<Vec3> Path,
    double Radius,
    double Depth,
    double TopHeight,
    Maybe<IMesh> Surface = default);

/// <summary>Procedural mesh generation.</summary>
public interface IGeometryGenerators
{
    /// <summary>An axis-aligned box spanning the two corner points.</summary>
    Result<IMesh> GenerateBox(Vec3 min, Vec3 max);

    /// <summary>A UV sphere. <paramref name="segments"/> controls both rings and slices.</summary>
    Result<IMesh> GenerateSphere(Vec3 centre, double radius, int segments = 16);

    /// <summary>A capped cylinder aligned to the Z axis.</summary>
    Result<IMesh> GenerateCylinder(Vec3 baseCentre, double radius, double height, int segments = 24);

    /// <summary>A tube swept along a path, its cross-section carried by parallel transport so it does not twist.</summary>
    Result<IMesh> GenerateTube(TubeSpec spec);

    /// <summary>
    /// Points along a circular arc of <paramref name="bendRadius"/> that starts at
    /// <paramref name="start"/> heading along <paramref name="startDirection"/> and turns until it
    /// heads along <paramref name="endDirection"/>. Nearly parallel directions need no bend and
    /// yield the start point alone.
    /// </summary>
    Result<ImmutableArray<Vec3>> GenerateArc(double bendRadius, Vec3 start, Vec3 startDirection, Vec3 endDirection, int segments);

    /// <summary>
    /// An open polyline resampled towards uniform <paramref name="spacing"/> and lightly smoothed.
    /// The end points are kept exactly; a path of fewer than three points is returned unchanged.
    /// </summary>
    Result<ImmutableArray<Vec3>> ResampleOpenPath(ImmutableArray<Vec3> path, double spacing, int smoothingIterations = 2);

    /// <summary>A closed solid following a path across a surface. See <see cref="DrapedPathSpec"/>.</summary>
    Result<IMesh> GenerateDrapedPath(DrapedPathSpec spec);
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

    /// <summary>Distinct undirected edges among the non-degenerate triangles.</summary>
    public int EdgeCount { get; init; }

    /// <summary>Vertices no triangle refers to. They describe nothing, and a compaction drops them.</summary>
    public int UnreferencedVertexCount { get; init; }
}

/// <summary>Measurement and inspection of meshes.</summary>
public interface IGeometryEvaluators
{
    Result<MeshStatistics> GetStatistics(IMesh mesh);

    Result<TopologyValidation> ValidateTopology(IMesh mesh);

    /// <summary>Splits a mesh into its connected components; a single component returns one mesh.</summary>
    Result<ImmutableArray<IMesh>> SeparateComponents(IMesh mesh);

    /// <summary>
    /// The unit normal of every vertex, in vertex order, weighted by the area of the faces around
    /// it. A vertex with no non-degenerate face gets the zero vector.
    /// </summary>
    Result<ImmutableArray<Vec3>> ComputeVertexNormals(IMesh mesh);

    /// <summary>
    /// How many triangles pass through another triangle of the same mesh. Triangles that only
    /// touch - sharing an edge or a corner, or lying in one plane - are not counted.
    /// </summary>
    Result<int> CountSelfIntersections(IMesh mesh);
}

/// <summary>Transformations. Every method returns a new mesh.</summary>
public interface IGeometryTransforms
{
    Result<IMesh> Translate(IMesh mesh, Vec3 offset);

    Result<IMesh> Scale(IMesh mesh, Vec3 factors);

    Result<IMesh> Rotate(IMesh mesh, Direction axis, double radians);
}

/// <summary>The mesh file formats the IO slices read and write.</summary>
public enum MeshFileFormat
{
    Stl,
    Obj,
    Off,
    Ply,
    ThreeMf,
}

/// <summary>Maps file names to formats.</summary>
public static class MeshFileFormats
{
    public static Maybe<MeshFileFormat> FromPath(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".stl" => Maybe<MeshFileFormat>.Some(MeshFileFormat.Stl),
            ".obj" => Maybe<MeshFileFormat>.Some(MeshFileFormat.Obj),
            ".off" => Maybe<MeshFileFormat>.Some(MeshFileFormat.Off),
            ".ply" => Maybe<MeshFileFormat>.Some(MeshFileFormat.Ply),
            ".3mf" => Maybe<MeshFileFormat>.Some(MeshFileFormat.ThreeMf),
            _ => Maybe<MeshFileFormat>.None(),
        };
}

/// <summary>
/// The vendor extension a package's own metadata and reference object are marked with. A 3MF
/// consumer that does not know the namespace ignores both, which is what keeps the file valid.
/// </summary>
public sealed record PackageVendor(string Prefix, string NamespaceUri, string ReferenceRole);

/// <summary>
/// A 3MF package: the printable model, optionally a second non-printable reference mesh (the
/// state a model was derived from, say), and named metadata strings. Metadata names carry the
/// vendor prefix, as 3MF requires of anything outside its own vocabulary.
/// </summary>
public sealed record MeshPackage(
    IMesh Model,
    Maybe<IMesh> Reference,
    ImmutableDictionary<string, string> Metadata,
    PackageVendor Vendor);

/// <summary>Mesh file import and export.</summary>
public interface IGeometryIO
{
    /// <summary>Writes a mesh in the format its file extension names.</summary>
    Result Export(IMesh mesh, string filePath, bool overwrite = false);

    /// <summary>Reads a mesh in the format its file extension names. A 3MF yields its model object.</summary>
    Result<IMesh> Import(string filePath);

    /// <summary>Parses a mesh from file contents already in memory.</summary>
    Result<IMesh> Read(ReadOnlySpan<byte> data, MeshFileFormat format, string name);

    /// <summary>Encodes a mesh as file contents.</summary>
    Result<byte[]> Write(IMesh mesh, MeshFileFormat format);

    /// <summary>Parses a 3MF package, reference mesh and metadata included.</summary>
    Result<MeshPackage> ReadPackage(ReadOnlySpan<byte> data, string name, PackageVendor vendor);

    /// <summary>Encodes a 3MF package.</summary>
    Result<byte[]> WritePackage(MeshPackage package);
}

/// <summary>Operations that rebuild a surface. Every one returns a new mesh.</summary>
public interface IGeometryModifiers
{
    /// <summary>
    /// The surface lying <paramref name="distance"/> outside the input - inside, when negative -
    /// re-meshed from a signed distance field. <paramref name="cellSize"/> is the target edge
    /// length of the result; zero picks one from the mesh's size. The result is closed.
    /// </summary>
    Result<IMesh> Offset(IMesh mesh, double distance, double cellSize = 0);

    /// <summary>
    /// Offsets out by <paramref name="distance"/> and back, <paramref name="iterations"/> times.
    /// The round trip rounds away concave detail smaller than the distance.
    /// </summary>
    Result<IMesh> DoubleOffset(IMesh mesh, double distance, int iterations = 1, double cellSize = 0);

    /// <summary>
    /// Quadric edge collapse towards <paramref name="targetTriangleCount"/>. Collapses that would
    /// break the manifold or fold a face over are refused, so a coarse mesh may stop above the
    /// target rather than degrade.
    /// </summary>
    Result<IMesh> Decimate(IMesh mesh, int targetTriangleCount);

    /// <summary>Welds coincident vertices, drops degenerate and repeated faces and unused vertices.</summary>
    Result<IMesh> Repair(IMesh mesh);

    /// <summary>
    /// Re-cuts surfaces that pass through each other. A mesh the boolean kernel will not accept
    /// comes back unchanged apart from its metadata.
    /// </summary>
    Result<IMesh> RepairSelfIntersections(IMesh mesh);
}

/// <summary>Where a ray met a surface.</summary>
public readonly record struct RayHit(Vec3 Point, Vec3 Normal, double Distance, int Triangle);

/// <summary>
/// The point of a surface nearest a query. <see cref="Normal"/> is the geometric normal of the
/// triangle carrying it, or zero where that triangle is degenerate.
/// </summary>
public readonly record struct SurfacePoint(Vec3 Point, Vec3 Normal, double Distance, int Triangle);

/// <summary>
/// A mesh prepared for repeated spatial queries. Build one with
/// <see cref="ISpatialQueries.BuildIndex"/> and reuse it: building costs far more than a query.
/// Safe to query from several threads. Disposing releases any native acceleration structure
/// early; an index that is never disposed releases it when collected.
/// </summary>
public interface ISpatialIndex : IDisposable
{
    IMesh Mesh { get; }

    /// <summary>The nearest hit in front of <paramref name="origin"/>. The normal is the geometric one, whichever way it faces.</summary>
    Maybe<RayHit> Raycast(Vec3 origin, Direction direction);

    Maybe<SurfacePoint> ClosestPoint(Vec3 point);

    /// <summary>Distance to the surface, negative inside the solid.</summary>
    double SignedDistance(Vec3 point);

    /// <summary>
    /// <see cref="SignedDistance"/> for a batch, answered in one pass - natively, in parallel,
    /// where the native library is present.
    /// </summary>
    ImmutableArray<double> SignedDistances(ImmutableArray<Vec3> points);
}

/// <summary>Spatial queries against meshes.</summary>
public interface ISpatialQueries
{
    Result<ISpatialIndex> BuildIndex(IMesh mesh);
}

/// <summary>
/// A triangulation of planar outlines. <see cref="Triangles"/> indexes <see cref="Points"/>,
/// three per triangle, counter-clockwise.
/// </summary>
public sealed record PlanarTriangulation(ImmutableArray<Vec2> Points, ImmutableArray<int> Triangles);

/// <summary>
/// Planar polygon operations. Offsets, buffers and unions answer with a single outline: where
/// the operation splits a region into islands the largest is kept, since every consumer of
/// these wants one footprint.
/// </summary>
public interface IPolygonOperations
{
    /// <summary>The smoothed outline of a mesh's shadow on the XY plane, concavities included.</summary>
    Result<PlanarPolygon> ProjectOutline(IMesh mesh);

    /// <summary>The smoothed convex hull of a mesh's shadow on the XY plane.</summary>
    Result<PlanarPolygon> ProjectConvexHull(IMesh mesh);

    /// <summary>Grows the outer boundary by <paramref name="distance"/>, or insets it when negative, with rounded corners.</summary>
    Result<PlanarPolygon> Offset(PlanarPolygon polygon, double distance);

    /// <summary>Everything within <paramref name="distance"/> of an open path. A single point buffers into a disc.</summary>
    Result<PlanarPolygon> BufferPath(ImmutableArray<Vec2> path, double distance);

    /// <summary>The merged outer boundaries of overlapping polygons.</summary>
    Result<PlanarPolygon> Union(ImmutableArray<PlanarPolygon> polygons);

    /// <summary>A closed prism over the polygon, holes included, between two heights.</summary>
    Result<IMesh> Extrude(PlanarPolygon polygon, double zMin, double zMax);

    /// <summary>Reflects across the Y axis, (x, y) to (-x, y), reversing winding so orientation is preserved.</summary>
    PlanarPolygon MirrorX(PlanarPolygon polygon);

    /// <summary>Triangulates outlines. Holes are found by containment, not by winding or order.</summary>
    Result<PlanarTriangulation> Triangulate(ImmutableArray<PlanarPolygon> polygons);
}

/// <summary>
/// A solid built from planar outlines laid into a <see cref="SurfaceFrame"/>: the outlines'
/// caps sit at <paramref name="Sink"/> and <paramref name="Depth"/> + <paramref name="Overshoot"/>
/// along the normal. With a <paramref name="Surface"/>, the frame is walked across it so the
/// solid follows its curvature rather than a flat plane. <paramref name="MaxEdgeLength"/>
/// subdivides long outline edges first, so they can bend with the surface; zero leaves them.
/// </summary>
public sealed record DecalPrismSpec(
    ImmutableArray<PlanarPolygon> Outlines,
    SurfaceFrame Frame,
    double Depth,
    double Sink,
    double Overshoot,
    double MaxEdgeLength = 0,
    Maybe<IMesh> Surface = default);

/// <summary>A prism laid onto a surface, and what went wrong on the way if anything did.</summary>
public sealed record ProjectedDecal(IMesh Mesh, bool ExtendsPastSurface, bool SurfaceTooCurved);

/// <summary>Decals: text and other outlines turned into solids that sit on a surface.</summary>
public interface IDecalOperations
{
    Result<IMesh> BuildPrism(DecalPrismSpec spec);

    /// <summary>Moves every vertex of a prism onto the surface along the frame's normal, keeping its height above it.</summary>
    Result<ProjectedDecal> ProjectPrism(IMesh surface, SurfaceFrame frame, IMesh prism);
}
