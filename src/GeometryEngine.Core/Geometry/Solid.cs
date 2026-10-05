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
/// A description of a solid as booleans over meshes: a mesh, or two descriptions combined. It is
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
    // Closed: a description is a Leaf or a Combined, and nothing outside this file can add a third.
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
