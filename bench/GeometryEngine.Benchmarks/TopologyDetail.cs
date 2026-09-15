using GeometryEngine.Core.Geometry;
using GeometryEngine;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Names the individual elements a topology check objects to, rather than counting them.
/// Counts say a mesh is not manifold; only the elements say whether that verdict is right.
/// </summary>
internal static class TopologyDetail
{
    public static int Run(string file)
    {
        var engine = BspGeometryEngine.Create();
        var mesh = engine.IO.Import(TestMeshes.PathOf(file)).Value;

        Console.WriteLine($"{file}: {mesh.TriangleCount} triangles, {mesh.VertexCount} vertices");
        Console.WriteLine();

        var undirected = new Dictionary<(int, int), List<int>>();
        var directed = new Dictionary<(int, int), List<int>>();
        var signatures = new Dictionary<(int, int, int), List<int>>();

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = mesh.Triangles[t * 3];
            var b = mesh.Triangles[(t * 3) + 1];
            var c = mesh.Triangles[(t * 3) + 2];

            Add(signatures, Sorted(a, b, c), t);

            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
            {
                Add(directed, (from, to), t);
                Add(undirected, from < to ? (from, to) : (to, from), t);
            }
        }

        Console.WriteLine("-- undirected edges not shared by exactly two triangles --");
        foreach (var (edge, tris) in undirected.Where(e => e.Value.Count != 2))
        {
            Console.WriteLine($"   edge ({edge.Item1},{edge.Item2}) used {tris.Count}x by triangles [{string.Join(", ", tris)}]");
        }

        Console.WriteLine();
        Console.WriteLine("-- half-edges traversed more than once in the same direction --");
        foreach (var (edge, tris) in directed.Where(e => e.Value.Count > 1))
        {
            Console.WriteLine($"   half-edge {edge.Item1}->{edge.Item2} used {tris.Count}x by triangles [{string.Join(", ", tris)}]");
        }

        Console.WriteLine();
        Console.WriteLine("-- triangles sharing the same three vertices --");
        foreach (var (signature, tris) in signatures.Where(s => s.Value.Count > 1))
        {
            Console.WriteLine($"   vertices ({signature.Item1},{signature.Item2},{signature.Item3}) shared by triangles [{string.Join(", ", tris)}]");
            foreach (var t in tris)
            {
                var a = mesh.Triangles[t * 3];
                var b = mesh.Triangles[(t * 3) + 1];
                var c = mesh.Triangles[(t * 3) + 2];
                var area = (mesh.Vertices[b] - mesh.Vertices[a]).Cross(mesh.Vertices[c] - mesh.Vertices[a]).Length * 0.5;
                Console.WriteLine($"      triangle {t}: wound ({a},{b},{c}), area {area:E3}");
            }

            Console.WriteLine("      coordinates:");
            foreach (var v in new[] { signature.Item1, signature.Item2, signature.Item3 })
            {
                Console.WriteLine($"        v{v} = {mesh.Vertices[v]}");
            }
        }

        return 0;
    }

    private static void Add<TKey>(Dictionary<TKey, List<int>> map, TKey key, int triangle)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(triangle);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) { (a, b) = (b, a); }
        if (b > c) { (b, c) = (c, b); }
        if (a > b) { (a, b) = (b, a); }
        return (a, b, c);
    }
}
