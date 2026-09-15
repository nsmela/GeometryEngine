namespace GeometryEngine.Generators;

/// <summary>Errors the generator slices report.</summary>
internal static class GeneratorErrors
{
    public static readonly Error DegenerateBox =
        new("Generators.DegenerateBox", "A box needs a strictly positive extent on every axis.");

    public static readonly Error NonPositiveRadius =
        new("Generators.NonPositiveRadius", "A radius must be greater than zero.");

    public static readonly Error NonPositiveHeight =
        new("Generators.NonPositiveHeight", "A height must be greater than zero.");

    public static readonly Error TooFewSegments =
        new("Generators.TooFewSegments", "A revolved surface needs at least three segments.");
}

/// <summary>
/// A vertex-and-index accumulator shared by the generator slices. It is mutable
/// on purpose and never escapes: each slice fills one, hands it to
/// <see cref="ToMesh"/> and drops it. Immutability is a property of the values a
/// caller can observe, not a vow of abstinence inside a private factory.
/// </summary>
internal sealed class MeshBuilder
{
    private readonly ImmutableArray<Vec3>.Builder _vertices = ImmutableArray.CreateBuilder<Vec3>();
    private readonly ImmutableArray<int>.Builder _triangles = ImmutableArray.CreateBuilder<int>();

    public int AddVertex(Vec3 vertex)
    {
        _vertices.Add(vertex);
        return _vertices.Count - 1;
    }

    public void AddTriangle(int a, int b, int c)
    {
        if (a == b || b == c || c == a)
        {
            return;
        }

        _triangles.Add(a);
        _triangles.Add(b);
        _triangles.Add(c);
    }

    public void AddQuad(int a, int b, int c, int d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    public Result<IMesh> ToMesh(MeshMetadata metadata) =>
        ImmutableMesh.Create(_vertices.ToImmutable(), _triangles.ToImmutable(), metadata);
}

/// <summary>Ask for an axis-aligned box spanning two corners.</summary>
public sealed record BoxRequest(Vec3 Min, Vec3 Max);

internal sealed class BoxHandler
{
    public Result<IMesh> Handle(BoxRequest request)
    {
        var size = request.Max - request.Min;
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
        {
            return GeneratorErrors.DegenerateBox;
        }

        var (min, max) = (request.Min, request.Max);
        var builder = new MeshBuilder();

        // Corners are numbered so that bit 0 is X, bit 1 is Y and bit 2 is Z.
        var corners = new int[8];
        for (var i = 0; i < 8; i++)
        {
            corners[i] = builder.AddVertex(new Vec3(
                (i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y,
                (i & 4) == 0 ? min.Z : max.Z));
        }

        builder.AddQuad(corners[0], corners[4], corners[6], corners[2]); // -X
        builder.AddQuad(corners[1], corners[3], corners[7], corners[5]); // +X
        builder.AddQuad(corners[0], corners[1], corners[5], corners[4]); // -Y
        builder.AddQuad(corners[2], corners[6], corners[7], corners[3]); // +Y
        builder.AddQuad(corners[0], corners[2], corners[3], corners[1]); // -Z
        builder.AddQuad(corners[4], corners[5], corners[7], corners[6]); // +Z

        return builder.ToMesh(new MeshMetadata("box", "GeometryEngine.Generators"));
    }
}

/// <summary>Ask for a UV sphere.</summary>
public sealed record SphereRequest(Vec3 Centre, double Radius, int Segments);

internal sealed class SphereHandler
{
    public Result<IMesh> Handle(SphereRequest request)
    {
        if (request.Radius <= 0)
        {
            return GeneratorErrors.NonPositiveRadius;
        }

        if (request.Segments < 3)
        {
            return GeneratorErrors.TooFewSegments;
        }

        var slices = request.Segments;
        var rings = Math.Max(2, request.Segments / 2);
        var builder = new MeshBuilder();

        var north = builder.AddVertex(request.Centre + new Vec3(0, 0, request.Radius));
        var south = builder.AddVertex(request.Centre - new Vec3(0, 0, request.Radius));

        // One shared vertex ring per latitude, so the surface closes on itself.
        var grid = new int[rings - 1, slices];
        for (var ring = 1; ring < rings; ring++)
        {
            var polar = Math.PI * ring / rings;
            var z = Math.Cos(polar) * request.Radius;
            var ringRadius = Math.Sin(polar) * request.Radius;

            for (var slice = 0; slice < slices; slice++)
            {
                var azimuth = 2 * Math.PI * slice / slices;
                grid[ring - 1, slice] = builder.AddVertex(request.Centre + new Vec3(
                    Math.Cos(azimuth) * ringRadius,
                    Math.Sin(azimuth) * ringRadius,
                    z));
            }
        }

        for (var slice = 0; slice < slices; slice++)
        {
            var next = (slice + 1) % slices;

            builder.AddTriangle(north, grid[0, slice], grid[0, next]);
            builder.AddTriangle(south, grid[rings - 2, next], grid[rings - 2, slice]);

            for (var ring = 0; ring < rings - 2; ring++)
            {
                builder.AddQuad(grid[ring, slice], grid[ring + 1, slice], grid[ring + 1, next], grid[ring, next]);
            }
        }

        return builder.ToMesh(new MeshMetadata("sphere", "GeometryEngine.Generators"));
    }
}

/// <summary>Ask for a capped cylinder standing on the Z axis.</summary>
public sealed record CylinderRequest(Vec3 BaseCentre, double Radius, double Height, int Segments);

internal sealed class CylinderHandler
{
    public Result<IMesh> Handle(CylinderRequest request)
    {
        if (request.Radius <= 0)
        {
            return GeneratorErrors.NonPositiveRadius;
        }

        if (request.Height <= 0)
        {
            return GeneratorErrors.NonPositiveHeight;
        }

        if (request.Segments < 3)
        {
            return GeneratorErrors.TooFewSegments;
        }

        var builder = new MeshBuilder();
        var top = request.BaseCentre + new Vec3(0, 0, request.Height);

        var baseHub = builder.AddVertex(request.BaseCentre);
        var topHub = builder.AddVertex(top);

        var lower = new int[request.Segments];
        var upper = new int[request.Segments];

        for (var segment = 0; segment < request.Segments; segment++)
        {
            var azimuth = 2 * Math.PI * segment / request.Segments;
            var offset = new Vec3(Math.Cos(azimuth) * request.Radius, Math.Sin(azimuth) * request.Radius, 0);

            lower[segment] = builder.AddVertex(request.BaseCentre + offset);
            upper[segment] = builder.AddVertex(top + offset);
        }

        for (var segment = 0; segment < request.Segments; segment++)
        {
            var next = (segment + 1) % request.Segments;

            builder.AddQuad(lower[segment], lower[next], upper[next], upper[segment]);
            builder.AddTriangle(baseHub, lower[next], lower[segment]);
            builder.AddTriangle(topHub, upper[segment], upper[next]);
        }

        return builder.ToMesh(new MeshMetadata("cylinder", "GeometryEngine.Generators"));
    }
}

/// <summary>The <see cref="IGeometryGenerators"/> facade over the generator slices.</summary>
internal sealed class GeometryGenerators : IGeometryGenerators
{
    private readonly BoxHandler _box = new();
    private readonly SphereHandler _sphere = new();
    private readonly CylinderHandler _cylinder = new();

    public Result<IMesh> GenerateBox(Vec3 min, Vec3 max) => _box.Handle(new BoxRequest(min, max));

    public Result<IMesh> GenerateSphere(Vec3 centre, double radius, int segments = 16) =>
        _sphere.Handle(new SphereRequest(centre, radius, segments));

    public Result<IMesh> GenerateCylinder(Vec3 baseCentre, double radius, double height, int segments = 24) =>
        _cylinder.Handle(new CylinderRequest(baseCentre, radius, height, segments));
}
