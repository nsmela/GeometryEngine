using System.Collections.Immutable;
using System.Runtime.InteropServices;
using BasicResults;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;

namespace GeometryEngine.Internal.Native;

/// <summary>How a result was produced, so a caller can tell what it is holding.</summary>
internal enum ManifoldProvenance
{
    /// <summary>Both operands were valid 2-manifolds; the result is guaranteed watertight.</summary>
    Native,

    /// <summary>
    /// An operand was not a valid 2-manifold and was welded by Manifold's own merge before
    /// the operation. The result is still guaranteed watertight, but it describes geometry
    /// that differs from the input.
    /// </summary>
    NativeAfterMergingOperands,
}

/// <summary>The outcome of a native operation, with the provenance of the mesh it produced.</summary>
internal readonly record struct ManifoldOutcome(IMesh Mesh, ManifoldProvenance Provenance);

/// <summary>
/// The outcome of evaluating a <see cref="Solid"/>, with how many meshes had to be read into the
/// kernel to do it - one per distinct mesh, which is the saving the description exists to make.
/// </summary>
/// <param name="HandlesHeldAtRead">
/// How many native solids were still held when the root was read. One - the root itself - is
/// what lets the kernel fold a run of like steps into a single operation.
/// </param>
internal readonly record struct ManifoldEvaluation(ManifoldOutcome Outcome, int MeshesImported, int HandlesHeldAtRead);

/// <summary>
/// Executes Boolean operations through the native Manifold library.
/// Handles marshaling to and from <see cref="IMesh"/> and ensures every native
/// resource is deterministically freed, preventing native leaks.
///
/// No failure escapes as an exception. The library's contract is that every failure is a
/// value, and a P/Invoke breaks that contract loudly: a missing or mismatched native binary
/// raises <see cref="DllNotFoundException"/> or <see cref="EntryPointNotFoundException"/>
/// from the first call, which would otherwise tear down the caller rather than be reported.
///
/// Every native object is made in two steps: <c>manifold_alloc_*</c> reserves raw memory, and a
/// constructing call builds the object in it and returns that same pointer. A handle is only
/// ever taken from the constructing call's return value, never from the allocation. P/Invokes
/// bind lazily, so a binary missing a newer export throws from the constructing call itself;
/// taking the handle from the allocation would then send unconstructed memory through a
/// destructor in the cleanup below, which is a crash rather than the binding-mismatch failure
/// this class promises. The cost of the safe order is the few bytes of the raw allocation,
/// leaked only on that failure.
/// </summary>
internal static unsafe class ManifoldKernel
{
    /// <summary>The union of two meshes: a description one step long.</summary>
    public static Result<ManifoldOutcome> Union(IMesh left, IMesh right, MeshMetadata metadata) =>
        Evaluate(Solid.Of(left).Union(right), metadata).Map(evaluation => evaluation.Outcome);

    /// <summary>
    /// Combines every mesh in one native operation. For a subtraction the first mesh is the
    /// subject and the rest are taken away from it.
    /// </summary>
    public static Result<ManifoldOutcome> Batch(
        IReadOnlyList<IMesh> meshes, ManifoldOpType op, MeshMetadata metadata, SolidRetention retention = SolidRetention.None) =>
        Guarded(() =>
        {
            var operands = new List<IntPtr>(meshes.Count);
            var merged = false;
            var vector = IntPtr.Zero;
            var result = IntPtr.Zero;
            try
            {
                foreach (var input in meshes)
                {
                    var operand = Acquire(input, retention);
                    if (operand.IsFailure)
                    {
                        return Result.Failure<ManifoldOutcome>(operand.Error);
                    }

                    operands.Add(operand.Value.Handle);
                    merged |= operand.Value.Merged;
                }

                vector = ManifoldNative.manifold_manifold_empty_vec(ManifoldNative.manifold_alloc_manifold_vec());
                foreach (var operand in operands)
                {
                    ManifoldNative.manifold_manifold_vec_push_back(vector, operand);
                }

                result = ManifoldNative.manifold_batch_boolean(ManifoldNative.manifold_alloc_manifold(), vector, op);

                var status = ManifoldNative.manifold_status(result);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<ManifoldOutcome>(ManifoldErrors.OperationFailed(status));
                }

                var mesh = Produce(result, metadata, retention);
                return mesh.IsFailure
                    ? Result.Failure<ManifoldOutcome>(mesh.Error)
                    : Result.Success(new ManifoldOutcome(mesh.Value, Provenance(merged)));
            }
            finally
            {
                if (result != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(result);
                }

                // The vector holds copies, so deleting it leaves the operands to be deleted too.
                if (vector != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold_vec(vector);
                }

                foreach (var operand in operands)
                {
                    ManifoldNative.manifold_delete_manifold(operand);
                }
            }
        });

    /// <summary>
    /// Cuts a mesh along a plane in one native pass, capping both cut faces. The first outcome is
    /// the side the normal points to.
    /// </summary>
    public static Result<(ManifoldOutcome Front, ManifoldOutcome Back)> Split(
        IMesh mesh,
        Plane plane,
        MeshMetadata frontMetadata,
        MeshMetadata backMetadata,
        SolidRetention retention = SolidRetention.None) =>
        Guarded(() =>
        {
            if (mesh.IsEmpty)
            {
                return Result.Failure<(ManifoldOutcome, ManifoldOutcome)>(ManifoldErrors.EmptyOperand(mesh.Metadata.Name));
            }

            var operand = Acquire(mesh, retention);
            if (operand.IsFailure)
            {
                return Result.Failure<(ManifoldOutcome, ManifoldOutcome)>(operand.Error);
            }

            var front = IntPtr.Zero;
            var back = IntPtr.Zero;
            try
            {
                var n = plane.Normal.Vector;
                var halves = ManifoldNative.manifold_split_by_plane(
                    ManifoldNative.manifold_alloc_manifold(),
                    ManifoldNative.manifold_alloc_manifold(),
                    operand.Value.Handle,
                    n.X,
                    n.Y,
                    n.Z,
                    plane.Offset);
                (front, back) = (halves.First, halves.Second);

                foreach (var half in new[] { front, back })
                {
                    var status = ManifoldNative.manifold_status(half);
                    if (status != ManifoldError.NoError)
                    {
                        return Result.Failure<(ManifoldOutcome, ManifoldOutcome)>(ManifoldErrors.OperationFailed(status));
                    }
                }

                var frontMesh = Produce(front, frontMetadata, retention);
                if (frontMesh.IsFailure)
                {
                    return Result.Failure<(ManifoldOutcome, ManifoldOutcome)>(frontMesh.Error);
                }

                var backMesh = Produce(back, backMetadata, retention);
                if (backMesh.IsFailure)
                {
                    return Result.Failure<(ManifoldOutcome, ManifoldOutcome)>(backMesh.Error);
                }

                var provenance = Provenance(operand.Value.Merged);
                return Result.Success((new ManifoldOutcome(frontMesh.Value, provenance), new ManifoldOutcome(backMesh.Value, provenance)));
            }
            finally
            {
                foreach (var handle in new[] { front, back, operand.Value.Handle })
                {
                    if (handle != IntPtr.Zero)
                    {
                        ManifoldNative.manifold_delete_manifold(handle);
                    }
                }
            }
        });

    /// <summary>
    /// Evaluates a description in one native pass. Each distinct mesh is read into the kernel
    /// once; each step is composed from handles already there; and only the root is written back
    /// out. Manifold's booleans are lazy - composing one records it and computes nothing - so the
    /// work happens once, when the root is read, with the whole description in view.
    ///
    /// A handle is let go the moment the last step that uses it has been composed, and that is
    /// not housekeeping. Manifold folds a run of like operations into one - a block less nine
    /// tools becomes the block less the union of the nine - but only through steps it holds the
    /// sole reference to. A handle kept here is a second reference, and with it the run is
    /// computed one step at a time: measured at 2.0x to 2.7x the time on the mould in
    /// <c>bench query</c>, 1,235 ms against 451 ms at 100k triangles.
    ///
    /// Whatever is still held when this returns, by any path, is freed in the one
    /// <c>finally</c>. Nothing this call owns outlives it. What <see cref="SolidRetention.Keep"/>
    /// leaves with a mesh is a separate reference that the mesh owns, and this call only ever
    /// holds copies of it: see <see cref="RetainedSolid"/>.
    /// </summary>
    /// <remarks>
    /// The status is read at the root alone. Reading it at a step would force that step to be
    /// computed on its own, which is exactly the round trip this exists to avoid. The price is
    /// that an operation which fails is reported for the description, not for a step; a mesh the
    /// kernel will not take is still named, since that is known when it is read in.
    /// </remarks>
    public static Result<ManifoldEvaluation> Evaluate(
        Solid query, MeshMetadata metadata, SolidRetention retention = SolidRetention.None) =>
        Guarded(() =>
        {
            var order = SolidWalk.PostOrder(query);
            var uses = SolidWalk.Uses(order, query);

            // Handle -> the uses of it still to come. A handle leaves when that reaches zero.
            var held = new Dictionary<IntPtr, int>();
            var imported = new Dictionary<object, IntPtr>(ReferenceEqualityComparer.Instance);
            var built = new Dictionary<Solid, IntPtr>(ReferenceEqualityComparer.Instance);
            var merged = false;
            var readIn = 0;

            void Release(IntPtr handle)
            {
                if (--held[handle] == 0)
                {
                    held.Remove(handle);
                    ManifoldNative.manifold_delete_manifold(handle);
                }
            }

            try
            {
                foreach (var node in order)
                {
                    if (node is Solid.Leaf leaf)
                    {
                        var geometry = SolidWalk.GeometryOf(leaf.Mesh);
                        if (!imported.TryGetValue(geometry, out var known))
                        {
                            var operand = Acquire(leaf.Mesh, retention);
                            if (operand.IsFailure)
                            {
                                return Result.Failure<ManifoldEvaluation>(operand.Error);
                            }

                            readIn += operand.Value.ReadIn ? 1 : 0;
                            known = operand.Value.Handle;
                            held.Add(known, uses.OfGeometry[geometry]);
                            imported.Add(geometry, known);
                            merged |= operand.Value.Merged;
                        }

                        built.Add(node, known);
                        continue;
                    }

                    if (node is Solid.Transformed placed)
                    {
                        // Recorded on the solid, not applied to it: see ManifoldNative. A mesh
                        // kept in the kernel is therefore moved without being read in again.
                        var source = built[placed.Source];
                        var moved = Move(placed.Transform, source);
                        if (moved == IntPtr.Zero)
                        {
                            return Result.Failure<ManifoldEvaluation>(ManifoldErrors.UnknownTransform(placed.Transform));
                        }

                        held.Add(moved, uses.OfNode[node]);
                        built.Add(node, moved);
                        Release(source);
                        continue;
                    }

                    var step = (Solid.Combined)node;
                    var (left, right) = (built[step.Left], built[step.Right]);
                    var combined = Combine(step.Op, left, right);
                    if (combined == IntPtr.Zero)
                    {
                        return Result.Failure<ManifoldEvaluation>(ManifoldErrors.UnknownOperation(step.Op));
                    }

                    held.Add(combined, uses.OfNode[node]);
                    built.Add(node, combined);
                    Release(left);
                    Release(right);
                }

                var root = built[query];
                var heldAtRead = held.Count;

                var status = ManifoldNative.manifold_status(root);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<ManifoldEvaluation>(ManifoldErrors.OperationFailed(status));
                }

                var mesh = Produce(root, metadata, retention);
                return mesh.IsFailure
                    ? Result.Failure<ManifoldEvaluation>(mesh.Error)
                    : Result.Success(new ManifoldEvaluation(
                        new ManifoldOutcome(mesh.Value, Provenance(merged)), readIn, heldAtRead));
            }
            finally
            {
                foreach (var handle in held.Keys)
                {
                    ManifoldNative.manifold_delete_manifold(handle);
                }
            }
        });

    /// <summary>
    /// Reads a mesh in and keeps its solid with it, for an operation still to come. True where
    /// that was done now; false where the mesh already had one and nothing was.
    /// </summary>
    public static Result<bool> Prepare(IMesh mesh) =>
        Guarded(() =>
        {
            if (mesh is ImmutableMesh { Measurements.NativeSolid: RetainedSolid })
            {
                return Result.Success(false);
            }

            var operand = Acquire(mesh, SolidRetention.Keep);
            if (operand.IsFailure)
            {
                return Result.Failure<bool>(operand.Error);
            }

            // What is kept is a reference of its own; this one was only ever the means.
            ManifoldNative.manifold_delete_manifold(operand.Value.Handle);
            return Result.Success(operand.Value.ReadIn);
        });

    /// <summary>A solid under one transform, or zero for a transform that is not one of the three.</summary>
    private static IntPtr Move(SolidTransform transform, IntPtr solid)
    {
        switch (transform)
        {
            case SolidTransform.Translation shift:
                return ManifoldNative.manifold_translate(
                    ManifoldNative.manifold_alloc_manifold(), solid, shift.Offset.X, shift.Offset.Y, shift.Offset.Z);

            case SolidTransform.Scaling scale:
                return ManifoldNative.manifold_scale(
                    ManifoldNative.manifold_alloc_manifold(), solid, scale.Factors.X, scale.Factors.Y, scale.Factors.Z);

            case SolidTransform.Turn turn:
                // Where the rotation sends each axis is a column of its matrix, and asking the
                // rotation itself keeps this in step with the managed transform by construction.
                var (x, y, z) = (turn.Rotation.Apply(Vec3.UnitX), turn.Rotation.Apply(Vec3.UnitY), turn.Rotation.Apply(Vec3.UnitZ));
                return ManifoldNative.manifold_transform(
                    ManifoldNative.manifold_alloc_manifold(), solid, x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z, 0, 0, 0);

            default:
                return IntPtr.Zero;
        }
    }

    /// <summary>One lazy native boolean, or zero for an operation that is not one.</summary>
    private static IntPtr Combine(BooleanOp op, IntPtr left, IntPtr right) =>
        op switch
        {
            BooleanOp.Union => ManifoldNative.manifold_union(ManifoldNative.manifold_alloc_manifold(), left, right),
            BooleanOp.Subtract => ManifoldNative.manifold_difference(ManifoldNative.manifold_alloc_manifold(), left, right),
            BooleanOp.Intersect => ManifoldNative.manifold_intersection(ManifoldNative.manifold_alloc_manifold(), left, right),
            _ => IntPtr.Zero,
        };

    private static ManifoldProvenance Provenance(bool merged) =>
        merged ? ManifoldProvenance.NativeAfterMergingOperands : ManifoldProvenance.Native;

    /// <summary>
    /// Rounds creases while leaving flat surface where it is, by interpolating the surface through
    /// smooth tangents rather than by filtering vertices.
    /// </summary>
    /// <param name="keepSharperThan">
    /// Degrees. An edge whose dihedral angle exceeds this stays sharp, getting its own normal on
    /// each side; everything shallower is rounded. Large values round every crease.
    /// </param>
    /// <param name="tolerance">How far the interpolated surface may sit from the refined mesh.</param>
    public static Result<ManifoldOutcome> SmoothEdges(
        IMesh mesh, double keepSharperThan, double tolerance, MeshMetadata metadata) =>
        Guarded(() =>
        {
            if (mesh.IsEmpty)
            {
                return Result.Failure<ManifoldOutcome>(ManifoldErrors.EmptyOperand(mesh.Metadata.Name));
            }

            var operand = ToManifold(mesh);
            if (operand.IsFailure)
            {
                return Result.Failure<ManifoldOutcome>(operand.Error);
            }

            var normals = IntPtr.Zero;
            var tangents = IntPtr.Zero;
            var refined = IntPtr.Zero;
            try
            {
                normals = ManifoldNative.manifold_calculate_normals(
                    ManifoldNative.manifold_alloc_manifold(), operand.Value.Handle, 0, keepSharperThan);

                var status = ManifoldNative.manifold_status(normals);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<ManifoldOutcome>(ManifoldErrors.OperationFailed(status));
                }

                tangents = ManifoldNative.manifold_smooth_by_normals(ManifoldNative.manifold_alloc_manifold(), normals, 0);

                status = ManifoldNative.manifold_status(tangents);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<ManifoldOutcome>(ManifoldErrors.OperationFailed(status));
                }

                // Nothing has moved yet: the two calls above only recorded normals and tangents.
                // This is the one that interpolates, and so the one that changes the shape.
                refined = ManifoldNative.manifold_refine_to_tolerance(ManifoldNative.manifold_alloc_manifold(), tangents, tolerance);

                status = ManifoldNative.manifold_status(refined);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<ManifoldOutcome>(ManifoldErrors.OperationFailed(status));
                }

                if (ManifoldNative.manifold_num_tri(refined) == 0)
                {
                    return Result.Failure<ManifoldOutcome>(ManifoldErrors.EmptyResult);
                }

                var provenance = operand.Value.Merged
                    ? ManifoldProvenance.NativeAfterMergingOperands
                    : ManifoldProvenance.Native;

                var result = FromManifold(refined, metadata);
                return result.IsSuccess
                    ? Result.Success(new ManifoldOutcome(result.Value, provenance))
                    : Result.Failure<ManifoldOutcome>(result.Error);
            }
            finally
            {
                foreach (var handle in new[] { refined, tangents, normals, operand.Value.Handle })
                {
                    if (handle != IntPtr.Zero)
                    {
                        ManifoldNative.manifold_delete_manifold(handle);
                    }
                }
            }
        });

    /// <summary>
    /// Collapses geometry that describes no shape to within <paramref name="tolerance"/>, keeping
    /// a subset of the original vertices. Bounded error by construction: nothing moves further
    /// than the tolerance. It takes no triangle-count target, and how far it reduces is decided
    /// by the geometry - a shape with no redundant detail simplifies barely at all.
    /// </summary>
    public static Result<ManifoldOutcome> Simplify(IMesh mesh, double tolerance, MeshMetadata metadata) =>
        Guarded(() =>
        {
            if (mesh.IsEmpty)
            {
                return Result.Failure<ManifoldOutcome>(ManifoldErrors.EmptyOperand(mesh.Metadata.Name));
            }

            var operand = ToManifold(mesh);
            if (operand.IsFailure)
            {
                return Result.Failure<ManifoldOutcome>(operand.Error);
            }

            var simplified = IntPtr.Zero;
            try
            {
                simplified = ManifoldNative.manifold_simplify(ManifoldNative.manifold_alloc_manifold(), operand.Value.Handle, tolerance);

                var status = ManifoldNative.manifold_status(simplified);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<ManifoldOutcome>(ManifoldErrors.OperationFailed(status));
                }

                if (ManifoldNative.manifold_num_tri(simplified) == 0)
                {
                    return Result.Failure<ManifoldOutcome>(ManifoldErrors.EmptyResult);
                }

                var provenance = operand.Value.Merged
                    ? ManifoldProvenance.NativeAfterMergingOperands
                    : ManifoldProvenance.Native;

                var result = FromManifold(simplified, metadata);
                return result.IsSuccess
                    ? Result.Success(new ManifoldOutcome(result.Value, provenance))
                    : Result.Failure<ManifoldOutcome>(result.Error);
            }
            finally
            {
                if (simplified != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(simplified);
                }

                ManifoldNative.manifold_delete_manifold(operand.Value.Handle);
            }
        });

    /// <summary>
    /// Re-meshes the isosurface of a signed distance field lying <paramref name="level"/> from
    /// the surface - the managed-field route to an offset, used where the native distance field
    /// is unavailable.
    /// </summary>
    /// <param name="signedDistance">Distance to the surface, negative inside the solid. Called from several threads at once.</param>
    public static Result<IMesh> LevelSet(
        Func<Vec3, double> signedDistance,
        Vec3 min,
        Vec3 max,
        double edgeLength,
        double level,
        MeshMetadata metadata) =>
        Guarded(() =>
        {
            // Manifold keeps the region where the field is above the level, so the field is
            // handed over negated: positive inside the solid.
            ManifoldNative.SdfCallback callback = (x, y, z, _) => -signedDistance(new Vec3(x, y, z));

            var box = IntPtr.Zero;
            var solid = IntPtr.Zero;
            try
            {
                box = ManifoldNative.manifold_box(ManifoldNative.manifold_alloc_box(), min.X, min.Y, min.Z, max.X, max.Y, max.Z);

                solid = ManifoldNative.manifold_level_set(
                    ManifoldNative.manifold_alloc_manifold(), callback, box, edgeLength, -level, 0, IntPtr.Zero);

                // The delegate must outlive the native call; without this the JIT is free to
                // collect it while Manifold is still sampling through it.
                GC.KeepAlive(callback);

                var status = ManifoldNative.manifold_status(solid);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.OperationFailed(status));
                }

                return ManifoldNative.manifold_num_tri(solid) == 0
                    ? Result.Failure<IMesh>(ManifoldErrors.EmptyResult)
                    : FromManifold(solid, metadata);
            }
            finally
            {
                if (solid != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(solid);
                }

                if (box != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_box(box);
                }
            }
        });

    /// <summary>
    /// Extrudes planar contours into a closed solid between two heights. Manifold triangulates
    /// the caps and closes the walls itself, so the result is manifold by construction.
    /// </summary>
    /// <param name="contours">Outer boundaries wound counter-clockwise, holes clockwise.</param>
    public static Result<IMesh> Extrude(
        IReadOnlyList<IReadOnlyList<Vec2>> contours,
        double zMin,
        double zMax,
        MeshMetadata metadata) =>
        Guarded(() =>
        {
            var simplePolygons = new List<IntPtr>(contours.Count);
            var polygons = IntPtr.Zero;
            var extruded = IntPtr.Zero;
            var placed = IntPtr.Zero;

            try
            {
                foreach (var contour in contours)
                {
                    if (contour.Count < 3)
                    {
                        continue;
                    }

                    var points = new ManifoldNative.NativeVec2[contour.Count];
                    for (var i = 0; i < contour.Count; i++)
                    {
                        points[i] = new ManifoldNative.NativeVec2 { X = contour[i].X, Y = contour[i].Y };
                    }

                    fixed (ManifoldNative.NativeVec2* pPoints = points)
                    {
                        simplePolygons.Add(ManifoldNative.manifold_simple_polygon(
                            ManifoldNative.manifold_alloc_simple_polygon(), pPoints, (nuint)points.Length));
                    }
                }

                if (simplePolygons.Count == 0)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.EmptyOperand("extrusion contours"));
                }

                var handles = simplePolygons.ToArray();
                fixed (IntPtr* pHandles = handles)
                {
                    polygons = ManifoldNative.manifold_polygons(ManifoldNative.manifold_alloc_polygons(), pHandles, (nuint)handles.Length);
                }

                extruded = ManifoldNative.manifold_extrude(ManifoldNative.manifold_alloc_manifold(), polygons, zMax - zMin, 0, 0, 1, 1);

                var status = ManifoldNative.manifold_status(extruded);
                if (status != ManifoldError.NoError)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.OperationFailed(status));
                }

                if (ManifoldNative.manifold_num_tri(extruded) == 0)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.EmptyResult);
                }

                // Extrusion always starts at z = 0; lift it onto the requested range.
                placed = ManifoldNative.manifold_translate(ManifoldNative.manifold_alloc_manifold(), extruded, 0, 0, zMin);

                return FromManifold(placed, metadata);
            }
            finally
            {
                if (placed != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(placed);
                }

                if (extruded != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_manifold(extruded);
                }

                if (polygons != IntPtr.Zero)
                {
                    ManifoldNative.manifold_delete_polygons(polygons);
                }

                foreach (var polygon in simplePolygons)
                {
                    ManifoldNative.manifold_delete_simple_polygon(polygon);
                }
            }
        });

    /// <summary>Runs a native operation, turning the exceptions a P/Invoke can raise into failures.</summary>
    private static Result<T> Guarded<T>(Func<Result<T>> operation)
    {
        if (!ManifoldNative.IsAvailable)
        {
            return Result.Failure<T>(ManifoldErrors.Unavailable);
        }

        try
        {
            return operation();
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            // The library is absent or unusable on this machine. Ordinary on a platform
            // whose binaries do not ship, and a caller with a managed fallback should use it.
            return Result.Failure<T>(ManifoldErrors.Unusable(exception));
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or MarshalDirectiveException)
        {
            // The library loaded but does not have the shape this binding expects - a wrong
            // export name, or a signature that cannot be marshalled. That is a defect in
            // this code or a version mismatch, not a platform limitation, and it is reported
            // as its own failure so it cannot be mistaken for one: quietly substituting a
            // different kernel would hide exactly the bug that needs fixing.
            return Result.Failure<T>(ManifoldErrors.BindingMismatch(exception));
        }
    }

    /// <summary>
    /// A live native manifold owned by whoever asked for it, whether its geometry had to be
    /// welded to build it, and whether it was read in for this request or was already kept.
    /// </summary>
    private readonly record struct Operand(IntPtr Handle, bool Merged, bool ReadIn);

    /// <summary>
    /// A mesh as a native solid: a copy of the one kept with it where there is one, and otherwise
    /// read in - and, under <see cref="SolidRetention.Keep"/>, kept for the next request. Either
    /// way the handle returned is the caller's own, to free as it frees any other.
    /// </summary>
    private static Result<Operand> Acquire(IMesh mesh, SolidRetention retention)
    {
        if (retention == SolidRetention.Keep
            && mesh is ImmutableMesh { Measurements.NativeSolid: RetainedSolid kept })
        {
            return Result.Success(new Operand(kept.Copy(), kept.Merged, ReadIn: false));
        }

        var operand = ToManifold(mesh);
        if (operand.IsFailure || retention == SolidRetention.None)
        {
            return operand;
        }

        try
        {
            Keep(mesh, operand.Value.Handle, operand.Value.Merged);
            return operand;
        }
        catch
        {
            // The handle has no owner yet: the caller only takes it from a success.
            ManifoldNative.manifold_delete_manifold(operand.Value.Handle);
            throw;
        }
    }

    /// <summary>
    /// The mesh a finished solid stands for and, under <see cref="SolidRetention.Keep"/>, that
    /// solid kept with it - so a result used as the next operation's operand never leaves the
    /// kernel. The handle stays the caller's to free.
    /// </summary>
    /// <param name="solid">A solid whose status has been read, so it is evaluated and will not change.</param>
    private static Result<IMesh> Produce(IntPtr solid, MeshMetadata metadata, SolidRetention retention)
    {
        var mesh = FromManifold(solid, metadata);
        if (mesh.IsSuccess && retention == SolidRetention.Keep && !mesh.Value.IsEmpty)
        {
            // Welded on the way in or not, what came out is a solid the kernel made itself.
            Keep(mesh.Value, solid, merged: false);
        }

        return mesh;
    }

    /// <summary>
    /// Leaves a second reference to <paramref name="solid"/> with the mesh, unless one is there.
    /// A mesh that is not an <see cref="ImmutableMesh"/> has nowhere to keep one.
    /// </summary>
    private static void Keep(IMesh mesh, IntPtr solid, bool merged)
    {
        if (mesh is not ImmutableMesh immutable || immutable.Measurements.NativeSolid is not null)
        {
            return;
        }

        var candidate = new RetainedSolid(
            ManifoldNative.manifold_copy(ManifoldNative.manifold_alloc_manifold(), solid), merged, mesh.TriangleCount);

        // Another thread may have read the same mesh in meanwhile. One is kept; ours goes.
        if (!ReferenceEquals(immutable.Measurements.KeepNativeSolid(candidate), candidate))
        {
            candidate.Dispose();
        }
    }

    private static Result<Operand> ToManifold(IMesh mesh)
    {
        // An empty operand cannot be represented here. manifold_alloc_manifold only
        // allocates storage - it does not construct a Manifold - so handing the raw pointer
        // back would put uninitialised memory into a native boolean and then through a
        // native destructor. Callers reject empty operands before reaching this point;
        // saying so as a value keeps that guarantee local instead of assumed.
        if (mesh.VertexCount == 0 || mesh.TriangleCount == 0)
        {
            return Result.Failure<Operand>(ManifoldErrors.EmptyOperand(mesh.Metadata.Name));
        }

        var vertProps = new double[mesh.VertexCount * 3];
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var v = mesh.Vertices[i];
            vertProps[i * 3] = v.X;
            vertProps[(i * 3) + 1] = v.Y;
            vertProps[(i * 3) + 2] = v.Z;
        }

        var triVerts = new ulong[mesh.TriangleCount * 3];
        for (var i = 0; i < mesh.Triangles.Length; i++)
        {
            triVerts[i] = (ulong)mesh.Triangles[i];
        }

        var meshGl = IntPtr.Zero;
        var mergedMeshGl = IntPtr.Zero;
        var manifold = IntPtr.Zero;
        try
        {
            fixed (double* pVerts = vertProps)
            fixed (ulong* pTris = triVerts)
            {
                meshGl = ManifoldNative.manifold_meshgl64(
                    ManifoldNative.manifold_alloc_meshgl64(),
                    pVerts,
                    (nuint)mesh.VertexCount,
                    3,
                    pTris,
                    (nuint)mesh.TriangleCount);
            }

            manifold = ManifoldNative.manifold_of_meshgl64(ManifoldNative.manifold_alloc_manifold(), meshGl);

            var status = ManifoldNative.manifold_status(manifold);
            var merged = false;

            if (status != ManifoldError.NoError)
            {
                // The mesh is not a 2-manifold as supplied. Manifold's merge welds
                // near-coincident vertices, which closes the common case of a surface
                // exported with unshared vertices. It also *alters the geometry*, so the
                // caller is told: see ManifoldProvenance.
                ManifoldNative.manifold_delete_manifold(manifold);
                manifold = IntPtr.Zero;

                mergedMeshGl = ManifoldNative.manifold_meshgl64_merge(ManifoldNative.manifold_alloc_meshgl64(), meshGl);
                manifold = ManifoldNative.manifold_of_meshgl64(ManifoldNative.manifold_alloc_manifold(), mergedMeshGl);
                status = ManifoldNative.manifold_status(manifold);
                merged = status == ManifoldError.NoError;
            }

            if (status != ManifoldError.NoError)
            {
                return Result.Failure<Operand>(ManifoldErrors.InvalidMesh(mesh.Metadata.Name, status));
            }

            // Ownership passes to the caller.
            var operand = new Operand(manifold, merged, ReadIn: true);
            manifold = IntPtr.Zero;
            return Result.Success(operand);
        }
        finally
        {
            if (manifold != IntPtr.Zero)
            {
                ManifoldNative.manifold_delete_manifold(manifold);
            }

            if (mergedMeshGl != IntPtr.Zero)
            {
                ManifoldNative.manifold_delete_meshgl64(mergedMeshGl);
            }

            if (meshGl != IntPtr.Zero)
            {
                ManifoldNative.manifold_delete_meshgl64(meshGl);
            }
        }
    }

    private static Result<IMesh> FromManifold(IntPtr manifold, MeshMetadata metadata)
    {
        if (ManifoldNative.manifold_is_empty(manifold) != 0)
        {
            return ImmutableMesh.Create(ImmutableArray<Vec3>.Empty, ImmutableArray<int>.Empty, metadata);
        }

        var meshGl = IntPtr.Zero;
        try
        {
            meshGl = ManifoldNative.manifold_get_meshgl64(ManifoldNative.manifold_alloc_meshgl64(), manifold);

            var nVerts = (int)ManifoldNative.manifold_meshgl64_num_vert(meshGl);
            var nTris = (int)ManifoldNative.manifold_meshgl64_num_tri(meshGl);
            var vertPropsLen = (int)ManifoldNative.manifold_meshgl64_vert_properties_length(meshGl);
            var triVertsLen = (int)ManifoldNative.manifold_meshgl64_tri_length(meshGl);

            if (nVerts == 0 || nTris == 0)
            {
                return ImmutableMesh.Create(ImmutableArray<Vec3>.Empty, ImmutableArray<int>.Empty, metadata);
            }

            // Vertex properties are interleaved, and the position is only the first three of
            // however many a vertex carries. The stride is read from the library rather than
            // assumed to be three, so extra properties (normals, colours) cannot be silently
            // read as coordinates - which would yield a plausible-looking but wrong mesh.
            var stride = (int)ManifoldNative.manifold_meshgl64_num_prop(meshGl);
            if (stride < 3)
            {
                return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                    $"vertices carry {stride} properties, too few to hold a position"));
            }

            // Cross-check the stride against the buffer the library will fill, so a
            // disagreement between the two is reported rather than read out of bounds.
            if (vertPropsLen != nVerts * stride)
            {
                return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                    $"vertex property buffer of {vertPropsLen} does not hold {nVerts} vertices of {stride} properties"));
            }

            if (triVertsLen != nTris * 3)
            {
                return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                    $"triangle index buffer of {triVertsLen} does not describe {nTris} triangles"));
            }

            var outVertProps = new double[vertPropsLen];
            var outTriVerts = new ulong[triVertsLen];
            var mergeLength = (int)ManifoldNative.manifold_meshgl64_merge_length(meshGl);
            var mergeFrom = new ulong[mergeLength];
            var mergeTo = new ulong[mergeLength];

            fixed (double* pOutVerts = outVertProps)
            fixed (ulong* pOutTris = outTriVerts)
            fixed (ulong* pMergeFrom = mergeFrom)
            fixed (ulong* pMergeTo = mergeTo)
            {
                ManifoldNative.manifold_meshgl64_vert_properties((IntPtr)pOutVerts, meshGl);
                ManifoldNative.manifold_meshgl64_tri_verts((IntPtr)pOutTris, meshGl);

                if (mergeLength > 0)
                {
                    ManifoldNative.manifold_meshgl64_merge_from_vert((IntPtr)pMergeFrom, meshGl);
                    ManifoldNative.manifold_meshgl64_merge_to_vert((IntPtr)pMergeTo, meshGl);
                }
            }

            var vertices = new List<Vec3>(nVerts);
            for (var i = 0; i < nVerts; i++)
            {
                vertices.Add(new Vec3(
                    outVertProps[i * stride],
                    outVertProps[(i * stride) + 1],
                    outVertProps[(i * stride) + 2]));
            }

            // Manifold splits a position into several vertices wherever runs from different
            // operands meet at it, and records the split in the merge vectors rather than the
            // index buffer. Read as-is, every such seam is a ring of boundary edges: the solid is
            // closed, but its triangles do not say so. Applying the merge is what makes the
            // guarantee visible to everything downstream that pairs edges by index.
            var remap = new int[nVerts];
            for (var i = 0; i < nVerts; i++)
            {
                remap[i] = i;
            }

            for (var i = 0; i < mergeLength; i++)
            {
                if (mergeFrom[i] >= (ulong)nVerts || mergeTo[i] >= (ulong)nVerts)
                {
                    return Result.Failure<IMesh>(ManifoldErrors.UnexpectedLayout(
                        $"merge pair {i} refers past the {nVerts} vertices"));
                }

                remap[mergeFrom[i]] = (int)mergeTo[i];
            }

            var triangles = new List<int>(triVertsLen);
            for (var i = 0; i < triVertsLen; i++)
            {
                triangles.Add(remap[outTriVerts[i]]);
            }

            if (mergeLength == 0)
            {
                return ImmutableMesh.Create([.. vertices], [.. triangles], metadata);
            }

            // Merged-away vertices are left unreferenced; drop them.
            var (compactVertices, compactTriangles) = MeshCleanup.Compact(vertices, triangles);
            return ImmutableMesh.Create(compactVertices, compactTriangles, metadata);
        }
        finally
        {
            if (meshGl != IntPtr.Zero)
            {
                ManifoldNative.manifold_delete_meshgl64(meshGl);
            }
        }
    }
}

/// <summary>Failures the native kernel can report.</summary>
internal static class ManifoldErrors
{
    public static readonly Error Unavailable = new(
        "Manifold.Unavailable",
        "The native Manifold library is not available for this platform or deployment. " +
        "Prebuilt binaries ship for win-x64 only; place manifoldc alongside the assembly, " +
        "or use BspGeometryEngine.CreateManagedBsp() for the pure managed kernel.");

    public static Error Unusable(Exception exception) => new(
        "Manifold.Unavailable",
        $"The native Manifold library could not be loaded: {exception.Message}");

    /// <summary>
    /// The library is present but does not match this binding. Deliberately not one of the
    /// errors that falls back to the managed kernel: a wrong export name or an
    /// unmarshallable signature is a defect here, and substituting another kernel would
    /// turn it into a silent loss of the watertightness guarantee.
    /// </summary>
    public static Error BindingMismatch(Exception exception) => new(
        "Manifold.BindingMismatch",
        "The native Manifold library does not match this binding - it is likely a different " +
        $"version than the one expected (Manifold 3.x): {exception.Message}. " +
        "Use BspGeometryEngine.CreateManagedBsp() to run without it.");

    public static Error EmptyOperand(string name) => new(
        "Manifold.EmptyOperand",
        $"Mesh '{name}' has no geometry, so it cannot be used as a boolean operand.");

    public static Error InvalidMesh(string name, ManifoldError status) => new(
        "Manifold.InvalidMesh",
        $"Mesh '{name}' is not a valid manifold: {status}");

    public static Error OperationFailed(ManifoldError status) => new(
        "Manifold.OperationFailed",
        $"Boolean operation failed with status: {status}");

    public static Error UnknownOperation(BooleanOp op) => new(
        "Booleans.UnknownOperation",
        $"'{op}' is not a boolean operation.");

    public static Error UnknownTransform(SolidTransform transform) => new(
        "Manifold.UnknownTransform",
        $"'{transform.GetType().Name}' is not a transform the native kernel knows.");

    public static readonly Error EmptyResult =
        new("Manifold.EmptyResult", "The operation produced an empty solid.");

    public static Error UnexpectedLayout(string detail) => new(
        "Manifold.UnexpectedLayout",
        $"The native library returned a mesh in an unexpected layout: {detail}.");
}
