namespace GeometryEngine.Internal;

/// <summary>
/// A union-find over vertex indices, used to count the connected components (shells)
/// of a mesh. Path-compressed on find; unions toward the smaller root so the result is
/// deterministic.
/// </summary>
internal sealed class DisjointSet
{
    private readonly int[] _parent;

    public DisjointSet(int size)
    {
        _parent = new int[size];
        for (var i = 0; i < size; i++)
        {
            _parent[i] = i;
        }
    }

    public int Find(int index)
    {
        while (_parent[index] != index)
        {
            _parent[index] = _parent[_parent[index]];
            index = _parent[index];
        }

        return index;
    }

    public void Union(int left, int right)
    {
        var a = Find(left);
        var b = Find(right);
        if (a != b)
        {
            _parent[Math.Max(a, b)] = Math.Min(a, b);
        }
    }

    /// <summary>The number of distinct roots among the given vertex indices.</summary>
    public int CountRootsAmong(ImmutableArray<int> indices)
    {
        var roots = new HashSet<int>();
        foreach (var index in indices)
        {
            roots.Add(Find(index));
        }

        return roots.Count;
    }
}
