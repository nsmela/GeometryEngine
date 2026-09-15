using GeometryEngine.Booleans;

namespace GeometryEngine.Tests.Booleans;

[Suite("Booleans / real test files")]
public sealed class RealMeshTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string StlPath(string name) => Path.Combine(RepoRoot, "stl", name);

    private static string BenchPath(string name) => Path.Combine(RepoRoot, "bench", "files", name);

    private static IMesh LoadStl(string path)
    {
        var result = Fixtures.Engine.IO.Import(path);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Failed to import {path}: {result.Error}");
        }
        return result.Value;
    }

    [Fact]
    public void All_showcase_operand_files_import_cleanly_and_are_watertight()
    {
        var showcaseFiles = new[]
        {
            "01-operand-cube.stl",
            "02-operand-sphere.stl",
            "03-union-spheres.stl",
            "04-intersect-spheres.stl",
            "05-subtract-spheres.stl",
            "06-rounded-cube.stl",
            "07-csg-showcase.stl",
            "08-flanged-plate.stl",
        };

        foreach (var file in showcaseFiles)
        {
            var mesh = LoadStl(StlPath(file));
            Check.True(mesh.VertexCount > 0, $"{file} should have vertices");
            Check.True(mesh.TriangleCount > 0, $"{file} should have triangles");

            var stats = Fixtures.Engine.Evaluators.GetStatistics(mesh).Value;
            Check.True(stats.Volume > 0, $"{file} volume should be positive");
            Check.True(double.IsFinite(stats.Volume), $"{file} volume should be finite");

            var topology = Fixtures.TopologyOf(mesh);
            Check.True(topology.IsWatertight, $"{file} should be watertight (bnd={topology.BoundaryEdgeCount}, nm={topology.NonManifoldEdgeCount})");
            Check.Equal(0, topology.BoundaryEdgeCount);
            Check.Equal(0, topology.NonManifoldEdgeCount);
        }
    }

    /// <summary>
    /// Pins the distinction between the two defects a single "not watertight" flag used to
    /// conflate, using the three bench meshes that actually exhibit them.
    ///
    /// ear_bolus and larynx small are <em>closed</em> - no holes - but carry one coincident
    /// pair of triangles wound oppositely, so one edge has four incident faces. Their
    /// half-edges still pair up two-to-two, which is why the native kernel accepts them and
    /// returns clean output; only the stricter edge-manifold test here objects.
    ///
    /// mould_test is genuinely torn: a triangle with two free edges, and an edge used three
    /// times, which is an odd count and therefore cannot pair. That is why the native kernel
    /// really does reject it and the managed fallback produces the result.
    ///
    /// If these characteristics ever change, the mesh files changed - which is worth knowing.
    /// </summary>
    [Fact]
    public void Closed_and_edge_manifold_are_distinct_defects()
    {
        foreach (var name in new[] { "ear_bolus.stl", "larynx small.stl" })
        {
            var topology = Fixtures.TopologyOf(LoadStl(BenchPath(name)));

            Check.True(topology.IsClosed, $"{name} should have no holes");
            Check.Equal(0, topology.BoundaryEdgeCount);

            Check.False(topology.IsEdgeManifold, $"{name} should have a doubled face");
            Check.Equal(1, topology.NonManifoldEdgeCount);
            Check.Equal(1, topology.DuplicateFaceCount);

            // Strictly non-manifold, yet closed: the two are not the same question.
            Check.False(topology.IsWatertight, $"{name} is not watertight by the strict test");
        }

        var torn = Fixtures.TopologyOf(LoadStl(BenchPath("mould_test.stl")));

        Check.False(torn.IsClosed, "mould_test has genuine holes");
        Check.Equal(2, torn.BoundaryEdgeCount);
        Check.Equal(0, torn.DuplicateFaceCount);
    }

    /// <summary>
    /// IsClean is the conjunction of four independent questions, and this pins both that
    /// decomposition and that splitting it out did not change what IsClean means - the property
    /// is public, so its meaning is a compatibility promise.
    ///
    /// ear_bolus also documents why the winding count should not be read alone: its two winding
    /// violations come from the doubled face re-traversing half-edges its twin already used, not
    /// from an independently inverted triangle.
    /// </summary>
    [Fact]
    public void IsClean_decomposes_into_four_independent_questions()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(RepoRoot, "bench", "files"), "*.stl"))
        {
            var t = Fixtures.TopologyOf(LoadStl(path));
            var name = Path.GetFileName(path);

            var expected = t.BoundaryEdgeCount == 0
                && t.NonManifoldEdgeCount == 0
                && t.DegenerateTriangleCount == 0
                && t.DuplicateVertexCount == 0
                && t.InconsistentWindingEdgeCount == 0
                && t.DuplicateFaceCount == 0;

            Check.Equal(expected, t.IsClean);
            Check.Equal(t.IsWatertight && t.IsConsistentlyWound && !t.HasRedundantGeometry, t.IsClean);
            Check.True(t.IsClosed || !t.IsWatertight, $"{name}: watertight implies closed");
        }

        // A doubled face shows up in three of the four axes at once.
        var doubled = Fixtures.TopologyOf(LoadStl(BenchPath("ear_bolus.stl")));
        Check.True(doubled.IsClosed, "ear_bolus encloses a volume");
        Check.False(doubled.IsEdgeManifold, "ear_bolus has an over-used edge");
        Check.False(doubled.IsConsistentlyWound, "ear_bolus's duplicate re-traverses half-edges");
        Check.True(doubled.HasRedundantGeometry, "ear_bolus carries a repeated face");
    }

    [Fact]
    public void All_fifteen_clinical_bench_files_import_and_have_valid_geometry()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot, "bench", "files"), "*.stl");

        // Every file present is checked, rather than a fixed count: pinning the count makes
        // adding a mesh fail a test whose subject is per-mesh geometry, not inventory.
        Check.True(
            files.Length > 0,
            $"expected STL fixtures under {Path.Combine(RepoRoot, "bench", "files")}; " +
            "this test needs a source checkout, not a published output");

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var mesh = LoadStl(path);
            Check.True(mesh.VertexCount > 0, $"{name} should have vertices");
            Check.True(mesh.TriangleCount > 0, $"{name} should have triangles");

            var stats = Fixtures.Engine.Evaluators.GetStatistics(mesh).Value;
            Check.True(double.IsFinite(stats.Volume), $"{name} volume should be finite");
            Check.True(stats.BoundsMax.X >= stats.BoundsMin.X, $"{name} bounds X");
            Check.True(stats.BoundsMax.Y >= stats.BoundsMin.Y, $"{name} bounds Y");
            Check.True(stats.BoundsMax.Z >= stats.BoundsMin.Z, $"{name} bounds Z");
        }
    }

    [Fact]
    public void Showcase_cube_and_sphere_satisfy_boolean_volume_identities()
    {
        var cube = LoadStl(StlPath("01-operand-cube.stl"));
        var sphere = LoadStl(StlPath("02-operand-sphere.stl"));

        var union = Fixtures.Engine.Booleans.Union(cube, sphere).Value;
        var subtract = Fixtures.Engine.Booleans.Subtract(cube, sphere).Value;
        var intersect = Fixtures.Engine.Booleans.Intersect(cube, sphere).Value;
        var revSubtract = Fixtures.Engine.Booleans.Subtract(sphere, cube).Value;

        var vCube = Fixtures.VolumeOf(cube);
        var vSphere = Fixtures.VolumeOf(sphere);
        var vUnion = Fixtures.VolumeOf(union);
        var vSubtract = Fixtures.VolumeOf(subtract);
        var vIntersect = Fixtures.VolumeOf(intersect);
        var vRevSubtract = Fixtures.VolumeOf(revSubtract);

        // V(A u B) = V(A) + V(B) - V(A n B)
        Check.RelativelyClose(vCube + vSphere - vIntersect, vUnion, 1e-4);

        // V(A - B) = V(A) - V(A n B)
        Check.RelativelyClose(vCube - vIntersect, vSubtract, 1e-4);

        // V(B - A) = V(B) - V(A n B)
        Check.RelativelyClose(vSphere - vIntersect, vRevSubtract, 1e-4);

        // Topology checks
        Check.True(Fixtures.TopologyOf(union).IsWatertight);
        Check.True(Fixtures.TopologyOf(subtract).IsWatertight);
        Check.True(Fixtures.TopologyOf(intersect).IsWatertight);
        Check.True(Fixtures.TopologyOf(revSubtract).IsWatertight);

        Check.Equal(0, Fixtures.TopologyOf(union).NonManifoldEdgeCount);
        Check.Equal(0, Fixtures.TopologyOf(subtract).NonManifoldEdgeCount);
        Check.Equal(0, Fixtures.TopologyOf(intersect).NonManifoldEdgeCount);
    }

    [Fact]
    public void Clinical_scalp_mould_and_bolus_pair_satisfy_boolean_identities()
    {
        var mould = LoadStl(BenchPath("scalp_mould.stl"));
        var bolus = LoadStl(BenchPath("scalp_bolus.stl"));

        var union = Fixtures.Engine.Booleans.Union(mould, bolus).Value;
        var subtract = Fixtures.Engine.Booleans.Subtract(mould, bolus).Value;
        var intersect = Fixtures.Engine.Booleans.Intersect(mould, bolus).Value;

        var vMould = Fixtures.VolumeOf(mould);
        var vBolus = Fixtures.VolumeOf(bolus);
        var vUnion = Fixtures.VolumeOf(union);
        var vSubtract = Fixtures.VolumeOf(subtract);
        var vIntersect = Fixtures.VolumeOf(intersect);

        var scale = Math.Max(vMould, 1.0);
        Check.True(Math.Abs(vUnion - (vMould + vBolus - vIntersect)) / scale < 1e-4);
        Check.True(Math.Abs(vSubtract - (vMould - vIntersect)) / scale < 1e-4);

        Check.True(Fixtures.TopologyOf(union).IsWatertight);
        Check.True(Fixtures.TopologyOf(subtract).IsWatertight);
        Check.True(Fixtures.TopologyOf(intersect).IsWatertight);
    }

    /// <summary>
    /// The guarantee that matters for print: a result presented as coming from the native
    /// kernel really is watertight. mould_test.stl is the case that exercises it - its input
    /// is not a 2-manifold, so Manifold declines it and the managed BSP kernel produces the
    /// result instead, with boundary edges. That is acceptable; presenting it as a
    /// guaranteed-watertight native result would not be.
    /// </summary>
    [Fact]
    public void A_result_labelled_native_is_always_watertight()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(RepoRoot, "bench", "files"), "*.stl"))
        {
            var name = Path.GetFileName(path);
            var mesh = LoadStl(path);
            var stats = Fixtures.Engine.Evaluators.GetStatistics(mesh).Value;
            var centre = (stats.BoundsMin + stats.BoundsMax) * 0.5;
            var radius = 0.4 * Math.Max(stats.BoundsSize.X, Math.Max(stats.BoundsSize.Y, stats.BoundsSize.Z));

            var result = Fixtures.Engine.Booleans.Subtract(mesh, Fixtures.Sphere(centre, radius, 32));

            // Error may only be read once IsFailure is known, so the message is built lazily.
            Check.True(
                result.IsSuccess,
                result.IsFailure
                    ? $"{name} subtract should succeed, but failed: {result.Error}"
                    : $"{name} subtract should succeed");

            var producer = result.Value.Metadata.CreatedBy;
            var topology = Fixtures.TopologyOf(result.Value);

            Check.True(
                producer is ManifoldBooleanOperations.NativeProducer
                    or ManifoldBooleanOperations.NativeMergedProducer
                    or ManifoldBooleanOperations.FallbackProducer,
                $"{name}: unrecognised producer '{producer}'");

            if (producer != ManifoldBooleanOperations.FallbackProducer)
            {
                Check.True(
                    topology.IsWatertight,
                    $"{name}: result claims the native kernel ('{producer}') but is not watertight " +
                    $"(bnd={topology.BoundaryEdgeCount}, nm={topology.NonManifoldEdgeCount})");
            }
        }
    }

    [Fact]
    public void Real_bolus_mesh_self_operations_behave_analytically()
    {
        var mesh = LoadStl(BenchPath("chin_bolus.stl"));
        var vOriginal = Fixtures.VolumeOf(mesh);

        var unionSelf = Fixtures.Engine.Booleans.Union(mesh, mesh).Value;
        Check.RelativelyClose(vOriginal, Fixtures.VolumeOf(unionSelf), 1e-5);
        Check.True(Fixtures.TopologyOf(unionSelf).IsWatertight);

        var intersectSelf = Fixtures.Engine.Booleans.Intersect(mesh, mesh).Value;
        Check.RelativelyClose(vOriginal, Fixtures.VolumeOf(intersectSelf), 1e-5);
        Check.True(Fixtures.TopologyOf(intersectSelf).IsWatertight);

        var subtractSelf = Fixtures.Engine.Booleans.Subtract(mesh, mesh).Value;
        Check.True(subtractSelf.IsEmpty || Fixtures.VolumeOf(subtractSelf) < 1e-6);
    }

    [Fact]
    public void Chained_booleans_on_clinical_mesh_stay_watertight()
    {
        var chin = LoadStl(BenchPath("chin_bolus.stl"));
        var stats = Fixtures.Engine.Evaluators.GetStatistics(chin).Value;
        var centre = (stats.BoundsMin + stats.BoundsMax) * 0.5;

        // Cut 1: sphere hollow
        var cutterSphere = Fixtures.Sphere(centre, 15, 32);
        var step1 = Fixtures.Engine.Booleans.Subtract(chin, cutterSphere).Value;
        Check.True(Fixtures.TopologyOf(step1).IsWatertight);

        // Cut 2: cylinder bore through the result
        var cutterCylinder = Fixtures.Cylinder(centre - new Vec3(0, 0, 30), 8, 60, 32);
        var step2 = Fixtures.Engine.Booleans.Subtract(step1, cutterCylinder).Value;
        Check.True(Fixtures.TopologyOf(step2).IsWatertight);

        // Verify volume strictly decreased at each step
        Check.True(Fixtures.VolumeOf(step1) < stats.Volume);
        Check.True(Fixtures.VolumeOf(step2) < Fixtures.VolumeOf(step1));
    }

    [Fact]
    public void Real_mesh_boolean_result_survives_stl_export_and_reimport()
    {
        var cube = LoadStl(StlPath("01-operand-cube.stl"));
        var sphere = LoadStl(StlPath("02-operand-sphere.stl"));
        var result = Fixtures.Engine.Booleans.Intersect(cube, sphere).Value;

        var tempFile = Path.Combine(Path.GetTempPath(), $"meshcsg-test-{Guid.NewGuid():N}.stl");
        try
        {
            var export = Fixtures.Engine.IO.Export(result, tempFile);
            Check.True(export.IsSuccess);

            var reimported = Fixtures.Engine.IO.Import(tempFile).Value;
            Check.Equal(result.TriangleCount, reimported.TriangleCount);
            Check.RelativelyClose(Fixtures.VolumeOf(result), Fixtures.VolumeOf(reimported), 1e-5);
            Check.True(Fixtures.TopologyOf(reimported).IsWatertight);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
