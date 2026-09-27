# Fabolus smoothing comparison harnesses

Two headless generators that run the same STL through both smoothing pipelines, so their outputs
can be compared with `tools/Fabolus.MeshCompare` in the Fabolus repository. The findings are in
[`docs/fabolus-smoothing-main-vs-engine.md`](../../docs/fabolus-smoothing-main-vs-engine.md); this
file is how to re-run them.

| | reference | candidate |
|---|---|---|
| harness | `MainHarness` | `EngineHarness` |
| pipeline | `Fabolus.Core/Smoothing/MarchingCubesSmoothing.cs` on `nsmela/Fabolus@main` | `SmoothSettings.Apply` on `nsmela/Fabolus@feat/geometry-engine` |
| geometry | MeshLib voxel offsets + geometry3Sharp `Reducer` | `Modifiers.DoubleOffset` / `Offset` / `Decimate` |

Neither harness is part of the GeometryEngine package or any shipped output, and neither is
referenced by the library, the tests or the benchmarks.

`MainHarness/FabolusMain.cs` holds verbatim copies of the `main` functions the reference pipeline
is made of, with the file each came from named above it. Copies rather than a project reference:
`Fabolus.Core` on `main` targets `net8.0-windows` with `UseWPF`, which will not build off Windows,
and `MeshModel` is in that assembly. Nothing in the copied code touches WPF, and MeshLib's NuGet
package carries `linux-x64` natives beside the `win-x64` ones, so the reference pipeline runs here
unchanged.

## Run

```bash
dotnet build tools/smoothing-comparison/MainHarness   -c Release
dotnet build tools/smoothing-comparison/EngineHarness -c Release

F=../Fabolus/files      # or tests/files on feat/geometry-engine
INPUTS=("$F/chin_bolus.stl" "$F/ear_bolus.stl" "$F/eye_bolus.stl" "$F/larynx small.stl" \
        "$F/larynx_bolus.stl" "$F/nose_bolus.stl" "$F/scalp_bolus.stl" "$F/small test.stl" \
        "$F/sphere.stl" "$F/test bolus 107mL.stl" "$F/test bolus 7mm.stl")

# --centred-out writes the centred, unsmoothed input, so the original shares the smoothed frame.
dotnet tools/smoothing-comparison/MainHarness/bin/Release/net8.0/main-harness.dll \
    --out out/main --centred-out out/input "${INPUTS[@]}" > out/main-run.json

dotnet tools/smoothing-comparison/EngineHarness/bin/Release/net8.0/engine-harness.dll \
    --out out/engine "${INPUTS[@]}" > out/engine-run.json

meshcompare batch --reference-dir out/main --candidate-dir out/engine --original-dir out/input \
    --icp --default-checks --csv out/head_to_head.csv --jsonl out/head_to_head.jsonl
```

Both harnesses default to the UI's standard preset — 1.0 mm offset cycle, 0.1 mm inflation, one
iteration, 1.0 mm cell, decimate to 2x the input triangles — and each flag overrides one of them
(`--deflate`/`--intensity`, `--inflate`/`--inflation`, `--iterations`, `--cell-size`/`--resolution`,
`--remesh-ratio`). Each writes one JSON document to stdout with per-case triangle counts, per-stage
timings and, on the reference side, the voxel size MeshLib chose for the un-parameterised inflation
offset. Exit code 1 means at least one case failed.

`EngineHarness` has three flags of its own:

- `--pipeline offset-smooth` swaps `DoubleOffset` for `Modifiers.OffsetSmooth`, the closing that
  iterates on the sampled field rather than re-meshing each round.
- `--separate` turns on the component splitting that `ImportMesh` does and the comparison
  otherwise leaves off, because `main` has no equivalent.
- `--raw-out <dir>` writes the offset surface as the level-set mesher produced it, before
  `Decimate` welds or collapses anything — what separates a defect in the offset from one in the
  decimation.

## Running off Windows

`Modifiers.Offset` needs `manifoldc` for its level-set mesher and the repository ships `win-x64`
binaries only, so a non-Windows run needs `libmanifoldc.so` and `libmanifold.so.3` built from
upstream Manifold at `7c86359` (the commit the shipped DLLs come from) and copied beside the
harness assembly, as `docs/decimate-vs-simplify.md` did. `geometryengine_native.so` is built from
this repository's own `native/`.

One trap, and it is silent: `native/src/manifold_dynamic.hpp` reaches `manifoldc` through
`dlopen("libmanifoldc.so")` with no path, while the managed resolver loads the versioned
`libmanifoldc.so.3` by absolute path — so the unversioned name is not resolvable through the
loader's own search path and `ge_offset` reports `ManifoldUnavailable`. `Offset` then falls back to
the managed field, whose cell budget is 50,000 against the native 400,000: the run succeeds, at a
coarser grid and several times slower. Export `LD_LIBRARY_PATH` to the directory holding the
natives and the native field engages. Check which one ran in the harness's `OffsetProducer` field
before trusting any timing.
