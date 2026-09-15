using GeometryEngine.Core.Geometry;
using GeometryEngine;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Dumps the full topology profile of every test mesh <em>as imported</em>, before any
/// boolean runs. `verify` only reports a yes/no for the input, which is not enough to tell
/// why a mesh the native kernel happily accepts is reported here as not watertight.
/// </summary>
internal static class TopologyReport
{
    public static int Run()
    {
        var engine = BspGeometryEngine.Create();

        Console.WriteLine(
            $"{"mesh",-24} {"tris",8} {"wt",4} {"bnd",7} {"nonmf",7} {"degen",7} " +
            $"{"dupvert",8} {"wind",7} {"dupface",8} {"shells",7}");
        Console.WriteLine(new string('-', 102));

        foreach (var path in TestMeshes.All())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var mesh = engine.IO.Import(path).Value;
            var t = engine.Evaluators.ValidateTopology(mesh).Value;

            Console.WriteLine(
                $"{name,-24} {mesh.TriangleCount,8} {(t.IsWatertight ? "y" : "N"),4} " +
                $"{t.BoundaryEdgeCount,7} {t.NonManifoldEdgeCount,7} {t.DegenerateTriangleCount,7} " +
                $"{t.DuplicateVertexCount,8} {t.InconsistentWindingEdgeCount,7} " +
                $"{t.DuplicateFaceCount,8} {t.ShellCount,7}");
        }

        return 0;
    }
}
