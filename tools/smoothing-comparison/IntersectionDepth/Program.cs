// How deep does each flagged self-intersection actually go?
//
//     depth <mesh.stl>
//
// For every triangle pair the counter flags, the penetration is the furthest a vertex of one
// lies beyond the other's plane, on the side it pierces. A surface that only grazes itself
// measures at the noise floor; one that genuinely crosses measures the depth of the crossing.
using GeometryEngine;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;

var engine = BspGeometryEngine.Create();
var mesh = engine.IO.Import(args[0]).Value;
var triangles = mesh.Triangles;

var min = mesh.Vertices[0];
var max = mesh.Vertices[0];
foreach (var v in mesh.Vertices) { min = min.ComponentMin(v); max = max.ComponentMax(v); }
var weld = (max - min).Length * 1e-7;

static double Penetration(Vec3 a0, Vec3 a1, Vec3 a2, Vec3 b0, Vec3 b1, Vec3 b2)
{
    var n = (a1 - a0).Cross(a2 - a0);
    var len = n.Length;
    if (len < 1e-300) return 0;
    n /= len;
    var d = -n.Dot(a0);
    double Below = 0, Above = 0;
    foreach (var v in new[] { b0, b1, b2 })
    {
        var s = n.Dot(v) + d;
        if (s < Below) Below = s;
        if (s > Above) Above = s;
    }

    // Straddling the plane by this much on the thinner side is how far it pierced.
    return Math.Min(-Below, Above);
}

var depths = new List<double>();
for (var i = 0; i < mesh.TriangleCount; i++)
{
    var (a0, a1, a2) = mesh.TriangleAt(i);
    for (var j = i + 1; j < mesh.TriangleCount; j++)
    {
        var shares = false;
        for (var p = 0; p < 3 && !shares; p++)
            for (var q = 0; q < 3 && !shares; q++)
                shares = triangles[i * 3 + p] == triangles[j * 3 + q];
        if (shares) continue;

        var (b0, b1, b2) = mesh.TriangleAt(j);
        if (!GeometryEngine.Internal.Spatial.TriangleIntersection.Intersects(a0, a1, a2, b0, b1, b2)) continue;

        depths.Add(Math.Max(Penetration(a0, a1, a2, b0, b1, b2), Penetration(b0, b1, b2, a0, a1, a2)));
    }
}

depths.Sort();
Console.WriteLine($"{Path.GetFileNameWithoutExtension(args[0]),-20} weld tolerance {weld:0.###e+00} mm   "
                  + $"{depths.Count} crossing pairs"
                  + (depths.Count == 0 ? "" :
                     $"   penetration min {depths[0]:0.###e+00}  median {depths[depths.Count / 2]:0.###e+00}  max {depths[^1]:0.###e+00} mm"));
