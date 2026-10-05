namespace GeometryEngine.Internal;

/// <summary>
/// The order in which a description is evaluated, shared by both kernels so they cannot disagree
/// about it: every part before whatever is built from it, and each part once.
/// </summary>
internal static class SolidWalk
{
    /// <summary>
    /// Every distinct node of the description, parts before wholes, the root last. A node reached
    /// by two paths - a common start extended twice - appears once, so it is evaluated once.
    /// </summary>
    /// <remarks>
    /// Walked with a stack of its own rather than by recursion. A description built in a loop is
    /// a tree as deep as the loop is long, and recursing down it would exhaust the thread's stack
    /// - which is not a failure that can be reported as a value.
    /// </remarks>
    public static IReadOnlyList<Solid> PostOrder(Solid root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var ordered = new List<Solid>();
        var seen = new HashSet<Solid>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(Solid Node, bool PartsDone)>();
        pending.Push((root, false));

        while (pending.Count > 0)
        {
            var (node, partsDone) = pending.Pop();
            if (partsDone)
            {
                ordered.Add(node);
                continue;
            }

            if (!seen.Add(node))
            {
                continue;
            }

            pending.Push((node, true));
            if (node is Solid.Combined combined)
            {
                // Right first, so the left is on top and is walked first: reading order.
                pending.Push((combined.Right, false));
                pending.Push((combined.Left, false));
            }
        }

        return ordered;
    }

    /// <summary>
    /// The meshes of a description, each once, in the order the description reads. The first is
    /// its subject: the mesh everything else is done to.
    /// </summary>
    public static IReadOnlyList<IMesh> Leaves(Solid root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var leaves = new List<IMesh>();
        foreach (var node in PostOrder(root))
        {
            if (node is Solid.Leaf leaf && seen.Add(GeometryOf(leaf.Mesh)))
            {
                leaves.Add(leaf.Mesh);
            }
        }

        return leaves;
    }

    /// <summary>
    /// What makes two meshes the same geometry for the purpose of reading it once. A copy made
    /// with <see cref="IMesh.WithMetadata"/> differs in name alone and shares its original's
    /// measurements, so the measurements identify the geometry; a mesh of any other kind is
    /// only ever the same as itself.
    /// </summary>
    public static object GeometryOf(IMesh mesh) =>
        mesh is ImmutableMesh immutable ? immutable.Measurements : mesh;
}
