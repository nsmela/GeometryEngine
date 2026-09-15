using BenchmarkDotNet.Attributes;
using GeometryEngine.Core.Geometry;
using GeometryEngine;

namespace GeometryEngine.Benchmarks;

/// <summary>
/// Times the three boolean operations on real bolus meshes cut with an overlapping
/// sphere, for both kernels.
///
/// Both backends are measured in the same run deliberately: comparing a figure from one
/// build against a figure from another leaves the machine, the runtime and the surrounding
/// code as uncontrolled variables, and the whole point of the comparison is to isolate the
/// kernel.
///
/// A caveat on the allocation column. <see cref="MemoryDiagnoserAttribute"/> counts
/// <em>managed</em> allocation only. That is the whole story for the BSP kernel, which is
/// pure managed, but for the native kernel it counts only the marshalling buffers either
/// side of the boundary and says nothing about what Manifold allocates internally. The two
/// allocation figures are therefore not comparable; the time figures are. Native retention
/// is measured separately by <c>soak</c> / <c>leak</c>.
/// </summary>
[MemoryDiagnoser]
public class BooleanBenchmarks
{
    private IGeometryEngine _engine = null!;
    private IMesh _mesh = null!;
    private IMesh _tool = null!;

    /// <summary>
    /// A spread of roughly an order of magnitude each: ~1.2k, ~3.2k, ~23k and ~100k
    /// triangles, so the shape of the scaling curve is visible and not just one point.
    /// All four are accepted by the native kernel (verify reports them as `native`), so the
    /// managed fallback never runs and the two rows really do measure two kernels.
    /// </summary>
    [Params("eye_bolus.stl", "chin_bolus.stl", "small test.stl", "test_smoothed_bolus.stl")]
    public string File { get; set; } = "eye_bolus.stl";

    [Params("manifold", "bsp")]
    public string Backend { get; set; } = "manifold";

    [GlobalSetup]
    public void Setup()
    {
        _engine = Backend == "bsp"
            ? BspGeometryEngine.CreateManagedBsp()
            : BspGeometryEngine.CreateWithManifold();

        _mesh = TestMeshes.Load(_engine, File);
        _tool = TestMeshes.OverlappingSphere(_engine, _mesh);
    }

    [Benchmark]
    public int Union() => _engine.Booleans.Union(_mesh, _tool).Value.TriangleCount;

    [Benchmark]
    public int Subtract() => _engine.Booleans.Subtract(_mesh, _tool).Value.TriangleCount;

    [Benchmark]
    public int Intersect() => _engine.Booleans.Intersect(_mesh, _tool).Value.TriangleCount;
}

/// <summary>Times import (binary and ASCII) and the round trip through export.</summary>
[MemoryDiagnoser]
public class ImportBenchmarks
{
    private readonly IGeometryEngine _engine = BspGeometryEngine.Create();
    private string _path = null!;

    [Params("chin_bolus.stl", "small test.stl")]
    public string File { get; set; } = "chin_bolus.stl";

    [GlobalSetup]
    public void Setup() => _path = TestMeshes.PathOf(File);

    [Benchmark]
    public int Import() => _engine.IO.Import(_path).Value.TriangleCount;
}
