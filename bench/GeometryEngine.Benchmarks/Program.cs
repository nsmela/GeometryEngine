using BenchmarkDotNet.Running;
using GeometryEngine.Benchmarks;

// Three modes:
//   (no args)        run the BenchmarkDotNet timing + allocation suite
//   verify           check the boolean volume identities on every test mesh
//   leak             probe for memory retained across repeated operations
//
// verify and leak are plain console runs (fast, exit-coded for CI); the default is the
// full statistical benchmark, which must be run in Release.
return (args.Length > 0 ? args[0] : "bench").ToLowerInvariant() switch
{
    "verify" => Verify.RunAll(),
    "topology" => TopologyReport.Run(),
    "topology-one" => TopologyDetail.Run(args[1]),
    "verify-one" => Verify.RunOne(args[1]),
    "leak" => LeakCheck.Run(),
    "latticescale" => LatticeScale.Run(),
    "offset" => OffsetProfile.Run(),
    "decimate" => DecimateCompare.Run(),
    "batch" => BatchCompare.Run(),
    "query" => QueryCompare.Run(),
    "retain" => RetainCompare.Run(),
    "retainsoak" => RetainSoak.Run(),
    "smoothfigures" => SmoothingFigures.Run(args.Length > 1 ? args[1] : "out"),
    _ => RunBenchmarks(args),
};

static int RunBenchmarks(string[] args)
{
    BenchmarkSwitcher.FromTypes([typeof(BooleanBenchmarks), typeof(ImportBenchmarks)]).Run(args);
    return 0;
}
