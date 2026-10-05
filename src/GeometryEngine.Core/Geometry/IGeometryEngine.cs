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

    /// <summary>
    /// Everything inside any of the meshes, in one operation. Cheaper than folding
    /// <see cref="Union(IMesh, IMesh)"/> over them - each pairwise step re-reads the growing
    /// result - and the meshes may overlap one another freely. One mesh comes back as it is.
    /// </summary>
    Result<IMesh> Union(ImmutableArray<IMesh> meshes);

    /// <summary>
    /// Everything inside <paramref name="mesh"/> and inside none of <paramref name="tools"/>, in
    /// one operation rather than a subtraction per tool against an ever-changing result. With no
    /// tools the mesh comes back as it is.
    /// </summary>
    Result<IMesh> Subtract(IMesh mesh, ImmutableArray<IMesh> tools);

    /// <summary>
    /// Cuts a closed mesh in two along a plane, capping both cut faces so each half is closed.
    /// <see cref="MeshSplit.Front"/> is the side the plane's normal points to. A mesh lying
    /// wholly on one side comes back whole on that side, with an empty mesh on the other.
    /// </summary>
    Result<MeshSplit> Split(IMesh mesh, Plane plane);

    /// <summary>
    /// Builds the solid a description stands for, in one pass: each mesh in it is read by the
    /// kernel once, however often the description uses it, and only the final solid is written
    /// back out. Cheaper than the same steps taken one call at a time, each of which hands its
    /// result back as a mesh for the next to read in again.
    ///
    /// Every mesh in the description must have geometry. A description that is a single mesh
    /// comes back as that mesh; one that describes nothing - a solid less itself - comes back as
    /// an empty mesh. A failure names the mesh the kernel would not take, where one is to blame,
    /// and otherwise speaks for the description as a whole rather than for one step of it.
    /// </summary>
    Result<IMesh> Evaluate(Solid query);

    /// <summary>
    /// Reads a mesh into the kernel ahead of its first use, where the kernel keeps what it reads:
    /// the first operation on the mesh then costs what every later one does. Reading a mesh in
    /// is about half of a boolean on it, and this is the way to pay that somewhere it will not
    /// be noticed - as a file loads, or on another thread while the user looks at the mesh.
    ///
    /// It is a hint, and safe to give for any mesh at any time, from any thread. An engine that
    /// keeps nothing has nothing to prepare and answers success. A mesh already read in is not
    /// read again.
    ///
    /// A failure is news about the mesh, not about the call: it has no geometry, or the native
    /// kernel will not take it as a solid - an open surface, say. Operations on such a mesh are
    /// still answered where there is a managed kernel to fall back to, without the native
    /// kernel's guarantee of a watertight result, and this is the earliest that can be known.
    /// </summary>
    Result Prepare(IMesh mesh);

    /// <summary>
    /// Lets go of what the kernel keeps for a mesh, now, without waiting for the mesh to be
    /// collected. For a caller that holds meshes it is not about to operate on - previews in an
    /// undo stack, say - where each would otherwise hold several times its own size in the
    /// kernel for as long as it is held.
    ///
    /// Like <see cref="Prepare"/> it is a hint, safe for any mesh at any time from any thread.
    /// The mesh is untouched and remains usable; the next operation on it reads it in again.
    /// Copies made with <see cref="IMesh.WithMetadata"/> share what is kept, so releasing one
    /// releases it for all of them. An operation already running keeps what it has taken.
    /// </summary>
    /// <returns>Whether anything was let go.</returns>
    bool Release(IMesh mesh);
}

/// <summary>
/// The two halves of a cut: <see cref="Front"/> on the side the plane's normal points to,
/// <see cref="Back"/> on the other. Either may be empty.
/// </summary>
public sealed record MeshSplit(IMesh Front, IMesh Back);

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

    public bool IsManifold => NonManifoldEdgeCount == 0 && DuplicateFaceCount == 0 && InconsistentWindingEdgeCount == 0;

    public bool HasCorruptTopology => DegenerateTriangleCount > 0 || DuplicateVertexCount > 0 || NonManifoldEdgeCount > 0 || InconsistentWindingEdgeCount > 0;

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

/// <summary>
/// Measurement and inspection of meshes.
///
/// <see cref="GetStatistics"/>, <see cref="ValidateTopology"/> and
/// <see cref="ComputeVertexNormals"/> remember their answer on the mesh, which cannot change, so
/// only the first call for a given geometry walks it and every later one is a lookup. A copy made
/// by <see cref="IMesh.WithMetadata"/> shares those answers, and a translation or rotation hands
/// them on to the mesh it produces. So there is no need to keep these figures beside a mesh: ask
/// again whenever they are needed - and, where the first measurement would land on a thread that
/// must not stall, ask once ahead of time on one that can.
/// </summary>
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

    /// <summary>
    /// How far each vertex of <paramref name="mesh"/> lies from the surface of
    /// <paramref name="reference"/> - how far smoothing or decimation moved a surface, say, when
    /// the reference is the mesh as it was before.
    /// </summary>
    Result<SurfaceDeviation> MeasureDeviation(IMesh mesh, IMesh reference);

    /// <summary>
    /// The local high points of a surface as seen along <paramref name="up"/>, with each one's
    /// prominence. Corners shared by several triangles are joined by position, so a mesh that
    /// repeats them per triangle reads as one surface rather than a heap of separate facets.
    /// </summary>
    Result<ISurfacePeaks> FindPeaks(IMesh mesh, Direction up);
}

/// <summary>
/// The signed distance of every vertex of a mesh from a reference surface, in the mesh's vertex
/// order: positive outside the reference, negative inside it.
///
/// It is one-sided and measured at vertices, so it is a floor on how far the two surfaces
/// differ rather than the whole of it: a bump on the reference that the mesh passes over without
/// a vertex near it goes unseen. Measure the other way round as well where that matters.
/// </summary>
/// <param name="MaxOutside">The furthest any vertex lies outside the reference; zero if none does.</param>
/// <param name="MaxInside">The furthest any vertex lies inside it, as a distance; zero if none does.</param>
/// <param name="MeanAbsolute">The average distance, whichever side.</param>
/// <param name="RootMeanSquare">The root mean square distance, which weighs the larger departures more.</param>
public sealed record SurfaceDeviation(
    ImmutableArray<double> Distances,
    double MaxOutside,
    double MaxInside,
    double MeanAbsolute,
    double RootMeanSquare);

/// <summary>Transformations. Every method returns a new mesh.</summary>
public interface IGeometryTransforms
{
    Result<IMesh> Translate(IMesh mesh, Vec3 offset);

    Result<IMesh> Scale(IMesh mesh, Vec3 factors);

    Result<IMesh> Rotate(IMesh mesh, Direction axis, double radians);

    /// <summary>Turns a mesh about the origin. For a rotation already held as a quaternion, or composed from several.</summary>
    Result<IMesh> Rotate(IMesh mesh, Rotation rotation);
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
    /// Taubin λ|μ fairing: reduces curvature by moving each vertex towards the centroid of its
    /// neighbours, with a second pass outwards that keeps the volume near where it started.
    ///
    /// Connectivity is untouched, so the triangle count is unchanged and the surface is never
    /// re-meshed - unlike <see cref="OffsetSmooth"/> this loses no detail to a sampling grid, and
    /// it neither closes a tear nor widens one. Vertices on an open edge are held still.
    /// </summary>
    /// <param name="iterations">Number of λ|μ pairs. Smoothing grows with this; cost is linear in it.</param>
    /// <param name="strength">λ, in (0, 1]: how far towards the neighbour centroid each pass moves a vertex.</param>
    Result<IMesh> LaplacianSmooth(IMesh mesh, int iterations = 5, double strength = 0.5);

    /// <summary>
    /// Morphological closing: inflates by <paramref name="distance"/> and deflates by the same,
    /// <paramref name="iterations"/> times, rounding away concave detail narrower than the
    /// distance. It bridges and fills, where <see cref="LaplacianSmooth"/> only fairs - this
    /// changes the shape rather than tidying it.
    ///
    /// The whole cycle runs on one sampled distance field: the mesh is read once at the start and
    /// written once at the end, whatever the iteration count. <see cref="DoubleOffset"/> does the
    /// same arithmetic but re-meshes between every step, so it pays the grid's cost once per pass
    /// instead of once in total. Measured on a bolus at 2 mm, its volume drifts -1.0 %, -2.3 %,
    /// -3.5 % over one, two and three rounds as the loss compounds, where this holds at +3.9 %,
    /// +3.7 %, +3.6 % - and the sign is the honest one for a closing, which fills rather than
    /// erodes. On a sphere, which has nothing to fill, the whole error is 0.5 % at one round and
    /// 0.3 % at three.
    ///
    /// Time follows from the same thing: the added rounds are grid sweeps, so the cost is roughly
    /// flat in <paramref name="iterations"/> where <see cref="DoubleOffset"/> grows linearly. It
    /// is not uniformly faster, though - at a single iteration it is somewhat slower, because it
    /// meshes on a finer default grid (64 cells across the longest side against 32) and so also
    /// returns several times as many triangles. Pass <paramref name="cellSize"/> if that matters.
    /// </summary>
    /// <param name="distance">How far to inflate and then deflate. Must be at least one grid cell.</param>
    /// <param name="iterations">Inflate/deflate rounds. Zero returns the mesh unchanged.</param>
    /// <param name="cellSize">Grid spacing; zero picks one from the mesh's size.</param>
    Result<IMesh> OffsetSmooth(IMesh mesh, double distance, int iterations = 1, double cellSize = 0);

    /// <summary>
    /// Subdivides the surface into a smooth interpolation of itself, treating the mesh as a
    /// control cage: vertex normals are shared across every edge shallower than
    /// <paramref name="keepSharperThan"/>, turned into tangents, and the surface is refined
    /// through them.
    ///
    /// <para><b>This is subdivision, not filleting, and the difference matters.</b> Flat surface
    /// is preserved exactly and costs no triangles - a patch through coplanar vertices with
    /// in-plane tangents is planar, and a cube's faces come back bit-identical. But where it does
    /// act on a crease it does not round the corner off: it bulges the whole neighbourhood
    /// outwards, by an amount set by the vertex spacing around that crease rather than by any
    /// radius. A 12-triangle cube smoothed at 120 degrees gains <b>168 % of its volume</b>. A
    /// boolean-built mould, whose edges are near 90 degrees, gains <b>209 %</b> and moves 40 % of
    /// its surface by more than a millimetre. Below 60 degrees the same mould is left alone
    /// (0.02 %), so the behaviour is a cliff, not a gradient: raise the angle past a model's real
    /// edges and it inflates.</para>
    ///
    /// <para>So this suits a coarse but already-smooth organic mesh, where there is no sharp edge
    /// to fall off. On a bolus at 30 degrees, 9 % of facets move more than 0.1 mm, the worst by
    /// 0.32 mm, for 6x the triangles. It is not the tool for rounding the sharp edges of a cut
    /// model while holding the rest accurate - <see cref="LaplacianSmooth"/> gated by dihedral
    /// angle would be, and is not implemented.</para>
    ///
    /// Torn input is refused outright, as the boolean kernel refuses it.
    /// </summary>
    /// <param name="keepSharperThan">
    /// Degrees of deviation from flat. An edge sharper than this keeps its own normal on each
    /// side and is left alone; anything shallower is smoothed. The default is deliberately
    /// conservative: measure the deviation before raising it past a model's real edges.
    /// </param>
    /// <param name="tolerance">
    /// How far the interpolated surface may sit from the mesh describing it - smaller refines
    /// further and costs more triangles. Zero scales one from the mesh's size.
    /// </param>
    Result<IMesh> SmoothEdges(IMesh mesh, double keepSharperThan = 30, double tolerance = 0);

    /// <summary>
    /// Rounds creases and leaves the rest of the surface exactly where it was.
    ///
    /// <para>Two restrictions on <see cref="LaplacianSmooth"/> do the work. Only vertices within a
    /// short reach of a fold sharper than <paramref name="roundSharperThan"/> are written at all,
    /// so flat and gently curved surface comes back bit-identical rather than merely close. And
    /// after every pass each moved vertex is pulled back inside a sphere of radius
    /// <paramref name="maxDeviation"/> about where it started, so the accuracy is a guarantee
    /// that holds whatever the iteration count, not a figure to be measured afterwards.</para>
    ///
    /// <para>Prefer this to <see cref="LaplacianSmooth"/> wherever the model has features worth
    /// keeping: ungated fairing touches the whole surface and will collapse a thin wall or a
    /// sharp rim. Prefer it to <see cref="SmoothEdges"/> wherever accuracy is the point, since
    /// that one preserves flat surface exactly but bulges a crease's neighbourhood by whatever
    /// the vertex spacing allows, with no bound. Triangle count is unchanged by this one.</para>
    ///
    /// <para><b>The gate is only selective on a mesh fine enough for ordinary curvature to fold
    /// less than the threshold.</b> Measured at 30 degrees across the bolus set, the share of
    /// vertices it admits runs from 9.5 % on a 100k-triangle mesh and 0 % on a smooth sphere up to
    /// 95 % on a 1.5k-triangle nose and 98 % on a coarse larynx - on those the facets themselves
    /// fold past 30 degrees, so nearly everything qualifies and the operation degenerates towards
    /// a capped version of <see cref="LaplacianSmooth"/>. It is still bounded, which the ungated
    /// filter is not, but it is no longer picking out creases. Raise the angle on a coarse mesh,
    /// or subdivide it first.</para>
    ///
    /// It rounds a crease; it does not cut a constant-radius fillet, which needs a CAD kernel.
    /// Where nothing is sharp enough to qualify, the mesh comes back with vertices identical to
    /// the ones it arrived with, and unlike <see cref="SmoothEdges"/> it accepts torn input.
    /// </summary>
    /// <param name="roundSharperThan">
    /// Degrees of fold, measured from flat: coplanar triangles read zero and a cube's edge reads
    /// ninety. Edges sharper than this are rounded; shallower ones are left alone.
    /// </param>
    /// <param name="maxDeviation">
    /// The furthest any vertex may travel from where it started, in model units. This is the
    /// bound, and it is enforced rather than hoped for. It is stated as displacement rather than
    /// as distance to the original surface on purpose: the latter lets a vertex slide along the
    /// surface without limit, which shears the shape while satisfying the bound.
    /// </param>
    /// <param name="iterations">λ|μ pairs. More rounds the crease further, up to the band.</param>
    /// <param name="strength">λ, in (0, 1]; see <see cref="LaplacianSmooth"/>.</param>
    Result<IMesh> SmoothCreases(
        IMesh mesh,
        double roundSharperThan = 30,
        double maxDeviation = 0.25,
        int iterations = 10,
        double strength = 0.5);

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
/// A mesh prepared for repeated spatial queries: building one costs far more than a query.
/// Usually the one to use is the mesh's own, from <see cref="ISpatialQueries.IndexFor"/>, which
/// is shared and cannot be disposed. One from <see cref="ISpatialQueries.BuildIndex"/> belongs
/// to the caller, and disposing it releases any native acceleration structure early; an index
/// that is never disposed releases it when collected. Safe to query from several threads.
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
    /// <summary>A new index over the mesh, owned by the caller, to dispose when done with it.</summary>
    Result<ISpatialIndex> BuildIndex(IMesh mesh);

    /// <summary>
    /// The index kept with the mesh: built the first time anything asks for it - this call, or
    /// an engine operation querying the mesh as a surface - and shared from then on, including
    /// with copies made by <see cref="IMesh.WithMetadata"/>. Prefer this for any mesh queried more
    /// than once; the engine's own operations already use it, so a decal laid onto a mesh and a
    /// deviation measured against it share one index rather than building two.
    ///
    /// It lives as long as the mesh does, and disposing it does nothing, so there is no owner to
    /// keep track of. A moved mesh is a new mesh, with an index of its own.
    /// </summary>
    Result<ISpatialIndex> IndexFor(IMesh mesh);
}

/// <summary>
/// A triangulation of planar outlines. <see cref="Triangles"/> indexes <see cref="Points"/>,
/// three per triangle, counter-clockwise.
/// </summary>
public sealed record PlanarTriangulation(ImmutableArray<Vec2> Points, ImmutableArray<int> Triangles);

/// <summary>
/// Planar polygon operations. Offsets, buffers and unions answer with a single outline: where
/// the operation splits a region into islands the largest is kept, since every consumer of
/// these wants one footprint. <see cref="Intersect"/> and <see cref="Subtract"/> are set
/// operations rather than footprint builders, and return every region.
/// </summary>
public interface IPolygonOperations
{
    /// <summary>The smoothed outline of a mesh's shadow on the XY plane, concavities included.</summary>
    Result<PlanarPolygon> ProjectOutline(IMesh mesh);

    /// <summary>The smoothed convex hull of a mesh's shadow on the XY plane.</summary>
    Result<PlanarPolygon> ProjectConvexHull(IMesh mesh);

    /// <summary>
    /// The exact convex hull of a set of points, counter-clockwise. Points spanning no area -
    /// fewer than three, or all in a line - have no hull and are refused; to give them one,
    /// <see cref="BufferPath"/> them instead, which turns a point into a disc and a line into a
    /// stadium.
    /// </summary>
    Result<PlanarPolygon> ConvexHull(ImmutableArray<Vec2> points);

    /// <summary>
    /// The regions inside both polygons, holes respected on the way in and out. Empty when they
    /// do not overlap - a valid answer, not an error.
    /// </summary>
    Result<ImmutableArray<PlanarPolygon>> Intersect(PlanarPolygon a, PlanarPolygon b);

    /// <summary>The regions inside <paramref name="a"/> and outside <paramref name="b"/>, holes respected. Empty when b covers a.</summary>
    Result<ImmutableArray<PlanarPolygon>> Subtract(PlanarPolygon a, PlanarPolygon b);

    /// <summary>
    /// Groups closed loops - a glyph's contours, say - into polygons, telling outlines from holes
    /// by containment: a loop inside an odd number of others is a hole of the loop directly
    /// around it, and an island inside a hole is a polygon of its own. Order and winding are
    /// ignored on the way in. Loops with fewer than three distinct points, or no area, are dropped.
    /// </summary>
    ImmutableArray<PlanarPolygon> FromLoops(ImmutableArray<ImmutableArray<Vec2>> loops);

    /// <summary>
    /// The cross-section where the horizontal plane at <paramref name="height"/> cuts a mesh, in
    /// XY: every closed loop of the cut, grouped into outlines and holes as <see cref="FromLoops"/>
    /// groups them. A hollow solid gives an outline with its cavity as a hole; a plane that misses
    /// the mesh gives nothing. Only closed loops are returned, so an open surface's cut is dropped
    /// where it runs off the edge.
    /// </summary>
    Result<ImmutableArray<PlanarPolygon>> Slice(IMesh mesh, double height);

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
///
/// <paramref name="SurfaceIndex"/> is the same surface already prepared for querying. Preparing
/// one costs far more than building a prism does, so a caller that builds prism after prism
/// against an unchanging surface - a label being dragged across a model, or several labels on
/// one - should build the index once and pass it here rather than passing the mesh and paying
/// for a new one every call. When it is set the mesh in <paramref name="Surface"/> is ignored.
/// </summary>
public sealed record DecalPrismSpec(
    ImmutableArray<PlanarPolygon> Outlines,
    SurfaceFrame Frame,
    double Depth,
    double Sink,
    double Overshoot,
    double MaxEdgeLength = 0,
    Maybe<IMesh> Surface = default,
    Maybe<ISpatialIndex> SurfaceIndex = default);

/// <summary>A prism laid onto a surface, and what went wrong on the way if anything did.</summary>
public sealed record ProjectedDecal(IMesh Mesh, bool ExtendsPastSurface, bool SurfaceTooCurved);

/// <summary>Decals: text and other outlines turned into solids that sit on a surface.</summary>
public interface IDecalOperations
{
    Result<IMesh> BuildPrism(DecalPrismSpec spec);

    /// <summary>Moves every vertex of a prism onto the surface along the frame's normal, keeping its height above it.</summary>
    Result<ProjectedDecal> ProjectPrism(IMesh surface, SurfaceFrame frame, IMesh prism);

    /// <summary>
    /// <see cref="ProjectPrism(IMesh, SurfaceFrame, IMesh)"/> against a surface already prepared
    /// for querying. Preparing one dominates the cost of a projection, so a caller projecting
    /// repeatedly onto an unchanging surface should build the index once and reuse it here.
    /// </summary>
    Result<ProjectedDecal> ProjectPrism(ISpatialIndex surface, SurfaceFrame frame, IMesh prism);
}
