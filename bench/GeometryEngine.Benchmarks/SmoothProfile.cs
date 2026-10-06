using System.Diagnostics;
using System.Runtime.InteropServices;
using GeometryEngine.Core.Geometry;
using GeometryEngine.Core.Geometry.Primitives;
using GeometryEngine.Internal.Native;
using GeometryEngine.Internal.Smoothing;
using GeometryEngine.Internal.Spatial;
using GeometryEngine.Modifiers;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Times an offset-smooth - a closing on a sampled distance grid - with the grid filled both
/// ways, and then phase by phase.
///
///   every node exactly   each grid node asked for its exact distance to the mesh, as it was.
///   near the surface     each node asked only whether the surface is within the closing's
///                        reach; a node beyond it takes the side of the node before it.
///
/// Both must hand the mesher the same grid, to the last bit, and the run says whether they did.
/// It then says how far the meshes agree, and how far two meshings of the same grid agree with
/// each other, because those are different questions: Manifold meshes a level set on its own
/// threads and numbers vertices and triangles in the order they finish, so on several cores one
/// grid can come back listed two ways. "Listed the same" is vertex for vertex in order; "the same
/// surface" is the same set of vertices and triangles whatever the order.
///
/// The phases are there because the two that matter scale differently with cores. Sampling runs
/// one grid plane to a task, and the level-set meshing is Manifold's, on its own threads. On one
/// core sampling every node was two thirds or more of the operation; what share is left to gain
/// on a machine with several is what this run is for.
///
/// Every timed call is on a mesh nothing has indexed, so each pays for its own index.
/// </summary>
internal static class SmoothProfile
{
    private const int Runs = 5;
    private const double Distance = 1.5;

    public static int Run()
    {
        var engine = BspGeometryEngine.CreateWithManifold(SolidRetention.None);
        Console.WriteLine($"Manifold native available: {ManifoldNative.IsAvailable}");
        Console.WriteLine($"logical processors: {Environment.ProcessorCount}");
        Console.WriteLine($"closing distance {Distance}, one iteration, cell left to the engine");

        foreach (var file in new[] { "ear_bolus.stl", "small test.stl", "test_smoothed_bolus.stl" })
        {
            var mesh = TestMeshes.Load(engine, file);
            IMesh Unseen() => engine.CreateMesh(mesh.Vertices, mesh.Triangles, mesh.Metadata).Value;

            var planned = OffsetSmoothHandler.GridFor(mesh, Distance, 0);
            Console.WriteLine();
            if (planned.IsFailure)
            {
                Console.WriteLine($"{file}: {mesh.TriangleCount:N0} triangles - not closed at this distance: {planned.Error}");
                continue;
            }

            var (min, max, cell, reach) = planned.Value;
            var tree = new MeshBvh(mesh);
            var exact = SignedDistanceGrid.Sample(tree.SignedDistance, min, max, cell);
            var within = 0;
            foreach (var value in exact.Values)
            {
                within += Math.Abs(value) < reach ? 1 : 0;
            }

            Console.WriteLine(
                $"{file}: {mesh.TriangleCount:N0} triangles, cell {cell:N3}, {exact.CellCount:N0} grid nodes, " +
                $"{within:N0} within reach ({reach:N2}) of the surface; " +
                $"sampled near the surface: {(OffsetSmoothHandler.CanSampleNearSurface(mesh) ? "yes" : "no, the mesh is not closed and consistently wound")}");

            var request = (IMesh input) => new OffsetSmoothRequest(input, Distance, 1, 0);
            var everyNode = new OffsetSmoothHandler(GridSampling.EveryNode);
            var nearSurface = new OffsetSmoothHandler(GridSampling.NearSurface);

            var before = everyNode.Handle(request(Unseen())).Value;
            var after = nearSurface.Handle(request(Unseen())).Value;

            Report("the whole closing / every node exactly", () =>
            {
                var input = Unseen();
                return () => everyNode.Handle(request(input));
            });
            Report("the whole closing / near the surface", () =>
            {
                var input = Unseen();
                return () => nearSurface.Handle(request(input));
            });
            Console.WriteLine(
                $"  result: {after.TriangleCount:N0} triangles, volume {engine.Evaluators.GetStatistics(after).Value.Volume:N3}");
            Agreement(mesh, Distance, 1, 0);

            Report("  phase: build the index", () => () => _ = new MeshBvh(mesh));
            Report("  phase: sample every node exactly", () => () => SignedDistanceGrid.Sample(tree.SignedDistance, min, max, cell));
            Report("  phase: sample near the surface", () => () => SignedDistanceGrid.SampleNear(tree, min, max, cell, reach));
            Report("  phase: inflate and deflate on the grid", () =>
            {
                var grid = SignedDistanceGrid.SampleNear(tree, min, max, cell, reach);
                return () =>
                {
                    grid.Shift(Distance);
                    grid.Reinitialise();
                    grid.Shift(-Distance);
                    grid.Reinitialise();
                };
            });
            Report("  phase: mesh the level set", () =>
            {
                var grid = SignedDistanceGrid.SampleNear(tree, min, max, cell, reach);
                grid.Shift(Distance);
                grid.Reinitialise();
                grid.Shift(-Distance);
                grid.Reinitialise();
                return () => ManifoldKernel.LevelSet(grid.Sample, min, max, cell, 0, MeshMetadata.Named("closed"));
            });
        }

        return 0;
    }

    /// <summary>
    /// The cases the tests close, which are the ones that showed two listings of one surface.
    /// </summary>
    public static int RunAgreement()
    {
        var engine = BspGeometryEngine.CreateWithManifold(SolidRetention.None);
        Console.WriteLine($"Manifold native available: {ManifoldNative.IsAvailable}");
        Console.WriteLine($"logical processors: {Environment.ProcessorCount}");

        var slotted = engine.Booleans.Subtract(
            engine.Generators.GenerateBox(new(0, 0, 0), new(20, 20, 20)).Value,
            engine.Generators.GenerateBox(new(9, -1, 10), new(11, 21, 21)).Value).Value;
        var shell = engine.Booleans.Subtract(
            engine.Generators.GenerateSphere(new(5, 5, 5), 14, 32).Value,
            engine.Generators.GenerateSphere(new(5, 5, 5), 4, 24).Value).Value;

        var cases = new (string Name, IMesh Mesh, double Distance, int Iterations, double CellSize)[]
        {
            ("slotted cube", slotted, 3, 1, 0),
            ("slotted cube", slotted, 3, 3, 0),
            ("slotted cube", slotted, 1.5, 2, 0.75),
            ("sphere", engine.Generators.GenerateSphere(new(0, 0, 0), 10, 48).Value, 2, 1, 0),
            ("hollow shell", shell, 1.5, 2, 0.75),
            ("ear_bolus.stl", TestMeshes.Load(engine, "ear_bolus.stl"), 2, 1, 0),
            ("chin_bolus.stl", TestMeshes.Load(engine, "chin_bolus.stl"), 3, 2, 0),
        };

        foreach (var (name, mesh, distance, iterations, cellSize) in cases)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{name}: distance {distance}, {iterations} iteration(s), cell {(cellSize > 0 ? cellSize.ToString() : "left to the engine")}; " +
                $"sampled near the surface: {(OffsetSmoothHandler.CanSampleNearSurface(mesh) ? "yes" : "no")}");
            Agreement(mesh, distance, iterations, cellSize);
        }

        return 0;
    }

    /// <summary>
    /// Says whether the two samplings give the mesher the same grid, and then - five times over -
    /// whether the meshes agree, between the samplings and between two runs of the exact one.
    /// </summary>
    private static void Agreement(IMesh mesh, double distance, int iterations, double cellSize)
    {
        const int Repeats = 5;
        var everyNode = new OffsetSmoothHandler(GridSampling.EveryNode);
        var nearSurface = new OffsetSmoothHandler(GridSampling.NearSurface);
        var plan = OffsetSmoothHandler.GridFor(mesh, distance, cellSize).Value;
        var request = new OffsetSmoothRequest(mesh, distance, iterations, cellSize);

        var sameGrid = MemoryMarshal.AsBytes(everyNode.Close(mesh, plan, distance, iterations).Values)
            .SequenceEqual(MemoryMarshal.AsBytes(nearSurface.Close(mesh, plan, distance, iterations).Values));
        Console.WriteLine($"  the grid handed to the mesher is the same either way: {(sameGrid ? "yes" : "NO")}");

        var (listedAcross, surfaceAcross, listedRepeat, surfaceRepeat) = (0, 0, 0, 0);
        for (var i = 0; i < Repeats; i++)
        {
            var exact = everyNode.Handle(request).Value;
            var again = everyNode.Handle(request).Value;
            var near = nearSurface.Handle(request).Value;

            listedAcross += ListedTheSame(exact, near) ? 1 : 0;
            surfaceAcross += TheSameSurface(exact, near) ? 1 : 0;
            listedRepeat += ListedTheSame(exact, again) ? 1 : 0;
            surfaceRepeat += TheSameSurface(exact, again) ? 1 : 0;
        }

        Console.WriteLine(
            $"  near the surface against every node:   listed the same {listedAcross} of {Repeats}, the same surface {surfaceAcross} of {Repeats}");
        Console.WriteLine(
            $"  every node against every node again:   listed the same {listedRepeat} of {Repeats}, the same surface {surfaceRepeat} of {Repeats}");
    }

    private static bool ListedTheSame(IMesh a, IMesh b) =>
        a.Vertices.AsSpan().SequenceEqual(b.Vertices.AsSpan()) && a.Triangles.AsSpan().SequenceEqual(b.Triangles.AsSpan());

    private static bool TheSameSurface(IMesh a, IMesh b)
    {
        var (verticesA, trianglesA) = Canonical(a);
        var (verticesB, trianglesB) = Canonical(b);
        return verticesA.AsSpan().SequenceEqual(verticesB) && trianglesA.AsSpan().SequenceEqual(trianglesB);
    }

    /// <summary>
    /// A mesh listed one fixed way: vertices in order of x, then y, then z; each triangle turned,
    /// without changing which way it faces, to start at its lowest vertex; triangles in order.
    /// </summary>
    private static (Vec3[] Vertices, (int, int, int)[] Triangles) Canonical(IMesh mesh)
    {
        var order = Enumerable.Range(0, mesh.VertexCount)
            .OrderBy(v => mesh.Vertices[v].X).ThenBy(v => mesh.Vertices[v].Y).ThenBy(v => mesh.Vertices[v].Z)
            .ToArray();
        var newIndex = new int[mesh.VertexCount];
        for (var i = 0; i < order.Length; i++)
        {
            newIndex[order[i]] = i;
        }

        var triangles = new (int, int, int)[mesh.TriangleCount];
        for (var t = 0; t < triangles.Length; t++)
        {
            var (a, b, c) = (newIndex[mesh.Triangles[t * 3]], newIndex[mesh.Triangles[(t * 3) + 1]], newIndex[mesh.Triangles[(t * 3) + 2]]);
            triangles[t] = a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);
        }

        Array.Sort(triangles);
        return (order.Select(v => mesh.Vertices[v]).ToArray(), triangles);
    }

    /// <param name="prepare">Builds one run's inputs, untimed, and returns the work to time.</param>
    private static void Report(string label, Func<Action> prepare)
    {
        prepare()(); // warm-up

        var times = new List<double>(Runs);
        for (var i = 0; i < Runs; i++)
        {
            var work = prepare();
            var watch = Stopwatch.StartNew();
            work();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        Console.WriteLine($"  {label,-46} {times[Runs / 2],9:N1} ms");
    }
}
