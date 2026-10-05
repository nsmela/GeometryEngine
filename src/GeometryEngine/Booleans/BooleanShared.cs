namespace GeometryEngine.Booleans;

/// <summary>Errors that any boolean slice can report.</summary>
internal static class BooleanErrors
{
    public static readonly Error EmptyOperand = MeshErrors.EmptyOperand;

    public static readonly Error NoOperands = new("Booleans.NoOperands", "A batch boolean needs at least one mesh.");

    public static Error KernelFailure(string description) =>
        new("Booleans.KernelFailure", description);

    public static Error UnknownOperation(BooleanOp op) =>
        new("Booleans.UnknownOperation", $"'{op}' is not a boolean operation.");
}

/// <summary>
/// The precondition every boolean slice shares: two non-empty operands.
/// Kept in one place so the three slices state it once between them.
/// </summary>
internal static class BooleanOperands
{
    public static Result Validate(IMesh left, IMesh right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.IsEmpty || right.IsEmpty
            ? Result.Failure(BooleanErrors.EmptyOperand)
            : Result.Success();
    }

    /// <summary>
    /// Names a result after the operation that produced it, as Fabolus does. The result is a new
    /// mesh rather than either operand, so its annotations are whatever the left operand's survive
    /// a <see cref="MeshOperation.Combine"/> - the left one because that is the one the operation
    /// reads as the subject ("A minus B"), and usually nothing at all.
    /// </summary>
    public static MeshMetadata DescribeResult(IMesh left, IMesh right, string operation) =>
        new($"{left.Metadata.Name} {operation} {right.Metadata.Name}", "GeometryEngine.Booleans")
        {
            Annotations = left.Metadata.Annotations?.Carry(MeshOperation.Combine),
        };

    /// <summary>The batch precondition: at least one operand, and every one of them non-empty.</summary>
    public static Result Validate(IReadOnlyList<IMesh> meshes)
    {
        if (meshes.Count == 0)
        {
            return Result.Failure(BooleanErrors.NoOperands);
        }

        foreach (var mesh in meshes)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            if (mesh.IsEmpty)
            {
                return Result.Failure(BooleanErrors.EmptyOperand);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// <see cref="DescribeResult"/> for a batch. Naming every operand would run to a paragraph
    /// for a label's worth of glyphs, so past two the rest are counted.
    /// </summary>
    public static MeshMetadata DescribeBatch(IReadOnlyList<IMesh> meshes, string operation) =>
        meshes.Count == 2
            ? DescribeResult(meshes[0], meshes[1], operation)
            : new MeshMetadata($"{meshes[0].Metadata.Name} {operation} {meshes.Count - 1} meshes", "GeometryEngine.Booleans")
            {
                Annotations = meshes[0].Metadata.Annotations?.Carry(MeshOperation.Combine),
            };

    /// <summary>
    /// <see cref="DescribeResult"/> for a description. One step between two meshes is named
    /// exactly as the pairwise call names it, so which way a caller asked cannot be read off the
    /// result. Anything longer would run to a sentence, so it is named for its subject - the mesh
    /// the description starts from - and a count of the others.
    /// </summary>
    public static MeshMetadata DescribeQuery(Solid query, IReadOnlyList<IMesh> leaves)
    {
        if (query is Solid.Combined { Left: Solid.Leaf left, Right: Solid.Leaf right } step)
        {
            return DescribeResult(left.Mesh, right.Mesh, step.Op.ToString());
        }

        var others = leaves.Count - 1;
        var company = others switch
        {
            0 => "itself",
            1 => "1 mesh",
            _ => $"{others} meshes",
        };

        return new MeshMetadata($"{leaves[0].Metadata.Name} combined with {company}", "GeometryEngine.Booleans")
        {
            Annotations = leaves[0].Metadata.Annotations?.Carry(MeshOperation.Combine),
        };
    }

    /// <summary>One half of a split: still the subject's geometry, rebuilt along the cut.</summary>
    public static MeshMetadata DescribeHalf(IMesh mesh, string side) =>
        mesh.Metadata
            .CarriedThrough(MeshOperation.Rebuild, "GeometryEngine.Booleans")
            .WithName($"{mesh.Metadata.Name} {side}");
}

/// <summary>
/// Batch operations and splits composed from pairwise booleans. The managed kernel has nothing
/// better, and the native one falls back to these on whatever it declines.
/// </summary>
internal static class ComposedBooleans
{
    /// <summary>
    /// Unions pairwise, level by level, so each step joins two results of similar size rather
    /// than folding every mesh into one ever-growing accumulator.
    /// </summary>
    public static Result<IMesh> Union(IBooleans booleans, IReadOnlyList<IMesh> meshes)
    {
        var level = meshes.ToList();
        while (level.Count > 1)
        {
            var next = new List<IMesh>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                if (i + 1 == level.Count)
                {
                    next.Add(level[i]);
                    continue;
                }

                var union = booleans.Union(level[i], level[i + 1]);
                if (union.IsFailure)
                {
                    return union;
                }

                next.Add(union.Value);
            }

            level = next;
        }

        return Result.Success(level[0]);
    }

    /// <summary>One subtraction per tool, in order.</summary>
    public static Result<IMesh> Subtract(IBooleans booleans, IMesh mesh, IReadOnlyList<IMesh> tools)
    {
        var current = mesh;
        foreach (var tool in tools)
        {
            var subtracted = booleans.Subtract(current, tool);
            if (subtracted.IsFailure)
            {
                return subtracted;
            }

            current = subtracted.Value;
        }

        return Result.Success(current);
    }

    /// <summary>
    /// The front half is what the mesh shares with a box filling the half-space in front of the
    /// plane, and the back half is the mesh less that box. The box is sized from the mesh rather
    /// than guessed: it reaches past every vertex, whatever the mesh's size or distance from the
    /// origin, and its back face lies exactly on the plane.
    /// </summary>
    public static Result<MeshSplit> Split(IBooleans booleans, IMesh mesh, Plane plane)
    {
        var box = HalfSpace(mesh, plane);
        if (box.IsFailure)
        {
            return Result.Failure<MeshSplit>(box.Error);
        }

        var front = booleans.Intersect(mesh, box.Value);
        if (front.IsFailure)
        {
            return Result.Failure<MeshSplit>(front.Error);
        }

        var back = booleans.Subtract(mesh, box.Value);
        if (back.IsFailure)
        {
            return Result.Failure<MeshSplit>(back.Error);
        }

        return new MeshSplit(
            front.Value.WithMetadata(BooleanOperands.DescribeHalf(mesh, "front").WithCreatedBy(front.Value.Metadata.CreatedBy)),
            back.Value.WithMetadata(BooleanOperands.DescribeHalf(mesh, "back").WithCreatedBy(back.Value.Metadata.CreatedBy)));
    }

    private static Result<IMesh> HalfSpace(IMesh mesh, Plane plane)
    {
        var min = mesh.Vertices[0];
        var max = min;
        foreach (var vertex in mesh.Vertices)
        {
            min = min.ComponentMin(vertex);
            max = max.ComponentMax(vertex);
        }

        // Every vertex lies within half the diagonal of the bounds' centre, and that centre within
        // the same of its own foot on the plane - so a square of this half-width about the foot,
        // this deep, contains everything in front.
        var reach = (max - min).Length + 1.0;
        var n = plane.Normal.Vector;
        var centre = (min + max) / 2;
        var foot = centre - (n * plane.SignedDistanceTo(centre));

        // Any vector not along the normal gives the frame; the least aligned axis is the most stable.
        var axis = Math.Abs(n.X) < 0.6 ? Vec3.UnitX : Math.Abs(n.Y) < 0.6 ? Vec3.UnitY : Vec3.UnitZ;
        var u = n.Cross(axis).Normalize();
        var v = n.Cross(u);
        var w = u.Cross(v); // n itself, recomputed so the frame is right-handed to the last bit

        return Generators.BoxHandler.InFrame(
            foot, u, v, w,
            new Vec3(-reach, -reach, 0),
            new Vec3(reach, reach, reach * 2),
            new MeshMetadata("half-space", "GeometryEngine.Booleans.Split"));
    }
}
