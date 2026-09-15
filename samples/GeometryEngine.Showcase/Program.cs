using System.Diagnostics;

namespace GeometryEngine.Showcase;

/// <summary>
/// Builds a handful of models with the engine, measures them and writes them out
/// as binary STL so they can be rendered. Everything here is ordinary use of the
/// public API: no internals are touched.
/// </summary>
internal static class Program
{
    private static readonly IGeometryEngine Engine = BspGeometryEngine.Create();

    private static int Main(string[] args)
    {
        var outputDirectory = args.Length > 0 ? args[0] : "out";
        Directory.CreateDirectory(outputDirectory);

        var models = new List<(string File, IMesh Mesh, string Recipe)>();

        var cube = Engine.Generators.GenerateBox(new Vec3(-1, -1, -1), new Vec3(1, 1, 1)).Value
            .WithMetadata(MeshMetadata.Named("cube"));
        var ball = Engine.Generators.GenerateSphere(Vec3.Zero, 1.32, 48).Value
            .WithMetadata(MeshMetadata.Named("ball"));

        models.Add(("01-operand-cube.stl", cube, "box(-1..1)"));
        models.Add(("02-operand-sphere.stl", ball, "sphere(r 1.32)"));

        var left = Engine.Generators.GenerateSphere(new Vec3(-0.45, 0, 0), 1, 40).Value
            .WithMetadata(MeshMetadata.Named("left"));
        var right = Engine.Generators.GenerateSphere(new Vec3(0.45, 0, 0), 1, 40).Value
            .WithMetadata(MeshMetadata.Named("right"));

        models.Add(("03-union-spheres.stl", Require(Engine.Booleans.Union(left, right)), "left u right"));
        models.Add(("04-intersect-spheres.stl", Require(Engine.Booleans.Intersect(left, right)), "left n right"));
        models.Add(("05-subtract-spheres.stl", Require(Engine.Booleans.Subtract(left, right)), "left - right"));

        var rounded = Require(Engine.Booleans.Intersect(cube, ball));
        models.Add(("06-rounded-cube.stl", rounded, "cube n ball"));

        var drill = DrillAssembly();
        models.Add(("07-csg-showcase.stl", Require(Engine.Booleans.Subtract(rounded, drill)), "(cube n ball) - 3 bores"));

        models.Add(("08-flanged-plate.stl", FlangedPlate(), "plate u boss - bore - 4 holes"));

        Report(models, outputDirectory);
        return 0;
    }

    /// <summary>Three cylinders crossing at the origin, one per axis.</summary>
    private static IMesh DrillAssembly()
    {
        var alongZ = Engine.Generators.GenerateCylinder(new Vec3(0, 0, -2), 0.56, 4, 64).Value;
        var alongX = Require(Engine.Transforms.Rotate(alongZ, Direction.Y, Math.PI / 2));
        var alongY = Require(Engine.Transforms.Rotate(alongZ, Direction.X, Math.PI / 2));

        return Require(Engine.Booleans.Union(Require(Engine.Booleans.Union(alongZ, alongX)), alongY));
    }

    /// <summary>A part built the way a CAD user would: add material, then remove it.</summary>
    private static IMesh FlangedPlate()
    {
        var plate = Engine.Generators.GenerateBox(new Vec3(-2, -2, 0), new Vec3(2, 2, 0.45)).Value
            .WithMetadata(MeshMetadata.Named("plate"));
        var boss = Engine.Generators.GenerateCylinder(new Vec3(0, 0, 0), 1.1, 1.3, 64).Value;
        var bore = Engine.Generators.GenerateCylinder(new Vec3(0, 0, -1), 0.6, 4, 64).Value;

        var part = Require(Engine.Booleans.Union(plate, boss));
        part = Require(Engine.Booleans.Subtract(part, bore));

        foreach (var corner in new[]
                 {
                     new Vec3(-1.45, -1.45, 0), new Vec3(1.45, -1.45, 0),
                     new Vec3(-1.45, 1.45, 0), new Vec3(1.45, 1.45, 0),
                 })
        {
            var hole = Engine.Generators.GenerateCylinder(corner + new Vec3(0, 0, -1), 0.28, 3, 32).Value;
            part = Require(Engine.Booleans.Subtract(part, hole));
        }

        return part.WithMetadata(MeshMetadata.Named("flanged plate"));
    }

    private static void Report(List<(string File, IMesh Mesh, string Recipe)> models, string outputDirectory)
    {
        Console.WriteLine();
        Console.WriteLine($"{"model",-24} {"recipe",-28} {"tris",7} {"verts",7} {"volume",12} {"watertight",11}");
        Console.WriteLine(new string('-', 95));

        foreach (var (file, mesh, recipe) in models)
        {
            var statistics = Engine.Evaluators.GetStatistics(mesh).Value;
            var topology = Engine.Evaluators.ValidateTopology(mesh).Value;
            var path = Path.Combine(outputDirectory, file);

            var export = Engine.IO.Export(mesh, path, overwrite: true);
            if (export.IsFailure)
            {
                Console.WriteLine($"  export failed: {export.Error}");
                continue;
            }

            Console.WriteLine(
                $"{Path.GetFileNameWithoutExtension(file),-24} {recipe,-28} {mesh.TriangleCount,7} {mesh.VertexCount,7} " +
                $"{statistics.Volume,12:0.0000} {(topology.IsWatertight ? "yes" : "NO"),11}");
        }

        Console.WriteLine();
    }

    private static IMesh Require(Result<IMesh> result)
    {
        if (result.IsFailure)
        {
            throw new UnreachableException(result.Error.ToString());
        }

        return result.Value;
    }
}
