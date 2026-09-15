namespace GeometryEngine.Tests;

/// <summary>Paths to the repository's mesh files, and small mesh-building helpers.</summary>
public static class Assets
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    });

    public static string BenchFile(string name) => Path.Combine(Root.Value, "bench", "files", name);

    public static IMesh LoadBench(string name)
    {
        var result = Fixtures.Engine.IO.Import(BenchFile(name));
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Failed to import {name}: {result.Error}");
        }

        return result.Value;
    }

    /// <summary>Both meshes' triangles in one mesh, without any boolean - overlaps stay overlapping.</summary>
    public static IMesh Concatenate(IMesh first, IMesh second)
    {
        var vertices = first.Vertices.AddRange(second.Vertices);
        var triangles = first.Triangles.AddRange(second.Triangles.Select(index => index + first.VertexCount));
        return Fixtures.Engine.CreateMesh(vertices, triangles, MeshMetadata.Named("concatenated")).Value;
    }

    /// <summary>A mesh with every triangle given its own three vertices, as an STL stores it.</summary>
    public static IMesh Unwelded(IMesh mesh)
    {
        var vertices = ImmutableArray.CreateBuilder<Vec3>(mesh.Triangles.Length);
        foreach (var index in mesh.Triangles)
        {
            vertices.Add(mesh.Vertices[index]);
        }

        return Fixtures.Engine.CreateMesh(
            vertices.MoveToImmutable(),
            [.. Enumerable.Range(0, mesh.Triangles.Length)],
            mesh.Metadata).Value;
    }

    public static ImmutableArray<Vec2> Square(double minX, double minY, double size, bool clockwise = false)
    {
        var ring = new[]
        {
            new Vec2(minX, minY), new Vec2(minX + size, minY), new Vec2(minX + size, minY + size), new Vec2(minX, minY + size),
        };

        return clockwise ? [.. ring.Reverse()] : [.. ring];
    }

    public static double AreaOf(PlanarPolygon polygon) =>
        Math.Abs(polygon.SignedArea) - polygon.Holes.Sum(hole => Math.Abs(PlanarPolygon.SignedAreaOf(hole)));
}
