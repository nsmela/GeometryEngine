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
    /// How many times each part of a description is used: once for every step built directly
    /// from it, and once more for the root, which is used by being read. A kernel that holds
    /// something per part knows from this when it has been used for the last time.
    /// </summary>
    /// <param name="order">The description's nodes as <see cref="PostOrder"/> gives them.</param>
    public static SolidUses Uses(IReadOnlyList<Solid> order, Solid root)
    {
        var ofNode = new Dictionary<Solid, int>(order.Count, ReferenceEqualityComparer.Instance);
        foreach (var node in order)
        {
            ofNode.Add(node, 0);
        }

        foreach (var node in order)
        {
            if (node is Solid.Combined combined)
            {
                ofNode[combined.Left]++;
                ofNode[combined.Right]++;
            }
        }

        ofNode[root]++;

        // A mesh may stand at several leaves, and is read into a kernel once for all of them.
        var ofGeometry = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        foreach (var node in order)
        {
            if (node is Solid.Leaf leaf)
            {
                var geometry = GeometryOf(leaf.Mesh);
                ofGeometry[geometry] = ofGeometry.GetValueOrDefault(geometry) + ofNode[node];
            }
        }

        return new SolidUses(ofNode, ofGeometry);
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

/// <summary>The uses of each node of a description, and of each distinct mesh across all its leaves.</summary>
internal sealed record SolidUses(IReadOnlyDictionary<Solid, int> OfNode, IReadOnlyDictionary<object, int> OfGeometry);
