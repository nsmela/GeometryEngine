namespace GeometryEngine.Core.Geometry;

/// <summary>The three ways two solids combine.</summary>
public enum BooleanOp
{
    /// <summary>Everything inside either.</summary>
    Union,

    /// <summary>Everything inside the left that is not inside the right.</summary>
    Subtract,

    /// <summary>Everything inside both.</summary>
    Intersect,
}

/// <summary>
/// How a part of a description is moved, turned or resized. A value like the description it
/// belongs to: making one moves nothing, and whether it can be applied at all - a scale that
/// would turn the solid inside out cannot - is answered when the description is evaluated.
/// </summary>
public abstract record SolidTransform
{
    // Closed, like Solid: these three and no others.
    private SolidTransform()
    {
    }

    /// <summary>Shifted by an offset.</summary>
    public sealed record Translation(Vec3 Offset) : SolidTransform;

    /// <summary>Turned about the origin.</summary>
    public sealed record Turn(Rotation Rotation) : SolidTransform;

    /// <summary>Scaled about the origin, by a factor along each axis.</summary>
    public sealed record Scaling(Vec3 Factors) : SolidTransform;
}

/// <summary>
/// A description of a solid as booleans over meshes: a mesh, or two descriptions combined, or
/// one moved. It is
/// a value, not an operation - building one does no geometry and cannot fail, and only
/// <see cref="IBooleans.Evaluate"/> turns it into a mesh.
///
/// That split is what the type is for. A chain of pairwise calls hands each intermediate result
/// back as a mesh, and the kernel then reads it in again for the next step - the dearest part of
/// a boolean, paid once per step. A description is handed over whole, so each mesh in it is read
/// once and only the final solid is written out.
///
/// Every method returns a new description and leaves this one as it was, so a description can be
/// extended in two directions from a common start, and the common start is evaluated once.
/// </summary>
/// <remarks>
/// Equality is structural and walks the whole tree, as does <see cref="object.ToString"/>. That
/// suits a test comparing two small descriptions; code that keys on a description by the
/// thousand should compare references instead.
/// </remarks>
public abstract record Solid
{
    // Closed: a description is a Leaf, a Combined or a Transformed, and nothing outside this file
    // can add a fourth.
    private Solid()
    {
    }

    /// <summary>The solid a mesh encloses.</summary>
    public static Solid Of(IMesh mesh) => new Leaf(mesh);

    /// <summary>Everything inside this solid or inside <paramref name="other"/>.</summary>
    public Solid Union(Solid other) => new Combined(BooleanOp.Union, this, other);

    /// <summary>Everything inside this solid that is not inside <paramref name="other"/>.</summary>
    public Solid Subtract(Solid other) => new Combined(BooleanOp.Subtract, this, other);

    /// <summary>Everything inside both this solid and <paramref name="other"/>.</summary>
    public Solid Intersect(Solid other) => new Combined(BooleanOp.Intersect, this, other);

    /// <inheritdoc cref="Union(Solid)"/>
    public Solid Union(IMesh other) => Union(Of(other));

    /// <inheritdoc cref="Subtract(Solid)"/>
    public Solid Subtract(IMesh other) => Subtract(Of(other));

    /// <inheritdoc cref="Intersect(Solid)"/>
    public Solid Intersect(IMesh other) => Intersect(Of(other));

    /// <summary>
    /// This solid shifted by <paramref name="offset"/>. Moving a part inside a description, where
    /// moving the mesh beforehand would do, spares the kernel reading the moved mesh in: it moves
    /// the solid it already has.
    /// </summary>
    public Solid Translate(Vec3 offset) => new Transformed(this, new SolidTransform.Translation(offset));

    /// <summary>This solid turned about an axis through the origin.</summary>
    public Solid Rotate(Direction axis, double radians)
    {
        ArgumentNullException.ThrowIfNull(axis);
        return Rotate(Rotation.FromAxisAngle(axis, radians));
    }

    /// <summary>This solid turned about the origin.</summary>
    public Solid Rotate(Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        return new Transformed(this, new SolidTransform.Turn(rotation));
    }

    /// <summary>
    /// This solid scaled about the origin. Every factor must be positive: a description holding
    /// one that is not is refused when it is evaluated.
    /// </summary>
    public Solid Scale(Vec3 factors) => new Transformed(this, new SolidTransform.Scaling(factors));

    /// <summary>A mesh, taken as the solid it encloses.</summary>
    public sealed record Leaf : Solid
    {
        public Leaf(IMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            Mesh = mesh;
        }

        public IMesh Mesh { get; }
    }

    /// <summary>A description, moved, turned or resized as a whole.</summary>
    public sealed record Transformed : Solid
    {
        public Transformed(Solid source, SolidTransform transform)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(transform);
            Source = source;
            Transform = transform;
        }

        public Solid Source { get; }

        public SolidTransform Transform { get; }
    }

    /// <summary>Two descriptions and the operation between them, read left to right.</summary>
    public sealed record Combined : Solid
    {
        public Combined(BooleanOp op, Solid left, Solid right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            Op = op;
            Left = left;
            Right = right;
        }

        public BooleanOp Op { get; }

        public Solid Left { get; }

        public Solid Right { get; }
    }
}
