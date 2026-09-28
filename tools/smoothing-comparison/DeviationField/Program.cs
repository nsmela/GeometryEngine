// Per-triangle signed distance from one mesh to another, for the heat maps in the PDF report.
//
// The field is sampled at each triangle's centroid of the SUBJECT mesh and measured against the
// TARGET mesh's surface, so the result has one value per subject triangle and drops straight into
// the renderer's per-facet colour argument. Positive means the subject lies outside the target.
//
// The engine's own ISpatialIndex answers it: SignedDistances takes the whole batch in one pass and
// runs natively and in parallel where the native library is present, which is what makes 11 cases
// x 3 fields tractable.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using GeometryEngine;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: deviation-field <subject.stl> <target.stl> <out.csv>");
    return 2;
}

var engine = BspGeometryEngine.Create();

var subject = engine.IO.Import(args[0]);
if (subject.IsFailure)
{
    Console.Error.WriteLine($"subject: {subject.Error}");
    return 3;
}

var target = engine.IO.Import(args[1]);
if (target.IsFailure)
{
    Console.Error.WriteLine($"target: {target.Error}");
    return 3;
}

var mesh = subject.Value;
var centroids = ImmutableArray.CreateBuilder<Vec3>(mesh.TriangleCount);
for (var t = 0; t < mesh.TriangleCount; t++)
{
    var (a, b, c) = mesh.TriangleAt(t);
    centroids.Add((a + b + c) / 3.0);
}

var index = engine.Spatial.BuildIndex(target.Value);
if (index.IsFailure)
{
    Console.Error.WriteLine($"index: {index.Error}");
    return 3;
}

using var spatial = index.Value;
var distances = spatial.SignedDistances(centroids.MoveToImmutable());

var text = new StringBuilder("signed_distance_mm\n");
foreach (var d in distances)
{
    text.Append(d.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
}

File.WriteAllText(args[2], text.ToString());

var sorted = distances.ToArray();
Array.Sort(sorted);
double Quantile(double q) => sorted[Math.Clamp((int)Math.Round(q * (sorted.Length - 1)), 0, sorted.Length - 1)];

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{Path.GetFileName(args[0])} vs {Path.GetFileName(args[1])}: n={sorted.Length} " +
    $"min={sorted[0]:F4} p5={Quantile(0.05):F4} median={Quantile(0.5):F4} p95={Quantile(0.95):F4} max={sorted[^1]:F4}"));

return 0;
