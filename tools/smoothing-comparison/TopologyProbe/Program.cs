// Where a non-manifold edge in a smoothed mesh actually comes from.
//
// The comparison measures exported STLs, which cannot distinguish a mesh that was already
// non-manifold from one that a position-weld made non-manifold on the way out. This asks the
// engine about its own meshes instead, at each stage, and counts the coincident vertex pairs -
// distinct indices at one position - that any position-weld downstream will fuse.
//
//     topology-probe <input.stl>...

using System.Collections.Immutable;
using GeometryEngine;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;

var engine = BspGeometryEngine.Create();

foreach (var path in args)
{
    var name = Path.GetFileNameWithoutExtension(path);
    var mesh = engine.IO.Import(path).Value;

    var stats = engine.Evaluators.GetStatistics(mesh).Value;
    var centre = (stats.BoundsMin + stats.BoundsMax) / 2.0;
    mesh = engine.Transforms.Translate(mesh, new Vec3((float)-centre.X, (float)-centre.Y, (float)-centre.Z)).Value;

    var closed = engine.Modifiers.DoubleOffset(mesh, 1.0, 1, 1.0).Value;
    var inflated = engine.Modifiers.Offset(closed, 0.1, 1.0).Value;

    void Report(string label, IMesh m)
    {
        var t = engine.Evaluators.ValidateTopology(m).Value;
        Console.WriteLine($"  {label,-28} verts={m.VertexCount,7} tris={m.TriangleCount,7} " +
                          $"boundary={t.BoundaryEdgeCount} nonmanifold={t.NonManifoldEdgeCount} " +
                          $"winding={t.InconsistentWindingEdgeCount} dupFaces={t.DuplicateFaceCount} " +
                          $"dupVerts={t.DuplicateVertexCount} shells={t.ShellCount} degenerate={t.DegenerateTriangleCount}");
    }

    Console.WriteLine($"{name}:");
    Report("in memory, after Offset", inflated);

    // How many distinct double vertices land on the same float32 triple? That is exactly what an
    // STL export fuses, and what a reader welding on exact coordinates then treats as one vertex.
    var buckets = new Dictionary<(float, float, float), int>();
    foreach (var v in inflated.Vertices)
    {
        var key = ((float)v.X, (float)v.Y, (float)v.Z);
        buckets[key] = buckets.TryGetValue(key, out var n) ? n + 1 : 1;
    }
    var collisions = inflated.VertexCount - buckets.Count;
    Console.WriteLine($"  float32 collisions           {collisions} vertex pairs fuse on export "
                      + $"({buckets.Count} distinct float32 positions for {inflated.VertexCount} vertices)");

    // The mesh Fabolus actually hands on: Decimate welds at a relative tolerance, so it sees
    // the coincident pair as one vertex where the level-set mesher kept them apart.
    var decimated = engine.Modifiers.Decimate(inflated, mesh.TriangleCount * 2).Value;
    Report("in memory, after Decimate", decimated);

    var tmp = Path.Combine(Path.GetTempPath(), name + "-probe.stl");
    engine.IO.Export(inflated, tmp, overwrite: true);
    Report("offset, re-imported STL", engine.IO.Import(tmp).Value);
    var tmp2 = Path.Combine(Path.GetTempPath(), name + "-probe-dec.stl");
    engine.IO.Export(decimated, tmp2, overwrite: true);
    Report("decimated, re-imported STL", engine.IO.Import(tmp2).Value);
    Console.WriteLine();
}
