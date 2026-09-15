namespace GeometryEngine.Internal.Planar;

/// <summary>
/// Builds a closed solid from a planar triangulation by giving every point a bottom and a top
/// position of its own: two copies of the triangulation as caps, and a wall along every
/// boundary edge. The extrusion, draped-path and decal slices differ only in where they put
/// those two positions, so this is the part they share.
/// </summary>
internal static class PrismBuilder
{
    public static Result<IMesh> Build(
        IReadOnlyList<(int A, int B, int C)> faces,
        IReadOnlyList<Vec3> bottoms,
        IReadOnlyList<Vec3> tops,
        MeshMetadata metadata)
    {
        var count = bottoms.Count;
        var vertices = ImmutableArray.CreateBuilder<Vec3>(count * 2);
        for (var i = 0; i < count; i++)
        {
            vertices.Add(bottoms[i]);
            vertices.Add(tops[i]);
        }

        static int Bottom(int point) => point * 2;
        static int Top(int point) => (point * 2) + 1;

        var triangles = ImmutableArray.CreateBuilder<int>((faces.Count * 6) + (count * 6));
        var directedEdges = new HashSet<(int, int)>();

        foreach (var (a, b, c) in faces)
        {
            // The top cap faces along the triangulation's winding, the bottom one against it.
            triangles.AddRange(Bottom(a), Bottom(c), Bottom(b));
            triangles.AddRange(Top(a), Top(b), Top(c));

            directedEdges.Add((a, b));
            directedEdges.Add((b, c));
            directedEdges.Add((c, a));
        }

        // An edge with no twin running the other way is on the boundary, and gets a wall.
        foreach (var (a, b) in directedEdges)
        {
            if (directedEdges.Contains((b, a)))
            {
                continue;
            }

            triangles.AddRange(Bottom(a), Bottom(b), Top(b));
            triangles.AddRange(Bottom(a), Top(b), Top(a));
        }

        return ImmutableMesh.Create(vertices.MoveToImmutable(), triangles.ToImmutable(), metadata);
    }

    /// <summary>A triangulation with every triangle turned counter-clockwise, whatever it arrived as.</summary>
    public static List<(int A, int B, int C)> CounterClockwise(IReadOnlyList<Vec2> points, IEnumerable<(int A, int B, int C)> faces) =>
        faces.Select(face => (points[face.B] - points[face.A]).Cross(points[face.C] - points[face.A]) < 0
                ? (face.A, face.C, face.B)
                : face)
            .ToList();
}
