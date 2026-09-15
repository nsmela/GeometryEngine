# Proposal: evaluate and improve mesh-operation performance and memory

> ## RESOLVED — 2026-09-10. Adopting Manifold settled this; the managed optimisation
> ## programme below is superseded.
>
> The question this brief poses — managed optimisation, parallelism, native, or GPU — was
> answered by measuring the native kernel against the managed one. Both backends were run in
> the same BenchmarkDotNet job (`--filter *BooleanBenchmarks* --job short`, Ryzen 5 5600,
> .NET 8), so the machine, runtime and surrounding code are controlled and only the kernel
> differs:
>
> | mesh | BSP subtract | Manifold subtract | speedup |
> |---|---:|---:|---:|
> | eye_bolus (1.2k tris) | 187.1 ms | 4.7 ms | **40x** |
> | chin_bolus (3.2k) | 499.2 ms | 7.0 ms | **71x** |
> | small test (23.5k) | 5653.2 ms | 42.5 ms | **133x** |
> | test_smoothed_bolus (100.6k) | 18864.6 ms | 132.9 ms | **142x** |
>
> Managed allocation falls from 34.5 GB to 6.3 MB per operation on the largest mesh, and gen0
> collections from ~2210 per operation to well under one. (The allocation figures are not
> like-for-like — the diagnoser counts managed bytes only, so Manifold's column is just the
> marshalling buffers — but the GC pressure removed is real, and it was the dominant cost.)
> Manifold also scales better: 84x the triangles costs it 28x the time, against 101x for the
> BSP kernel.
>
> **Consequences for the plan below.**
>
> - §4 managed optimisation, §5 native, §6 GPU, and the parallelism item in §7 are **moot for
>   the primary path**. They would optimise the BSP kernel, which is now only the fallback,
>   reached by 1 of 15 bench meshes (`mould_test.stl`, the one with a genuine hole) and by
>   platforms without native binaries.
> - The specific wins identified while profiling are recorded but **not worth doing**: a
>   `ClipPolygons` empty-input early-out measured at 2.3x faster / 2.25x less allocation with
>   byte-identical output, and a `ChooseDivider` fix worth up to 68% of a boolean on
>   structured input. Against 142x, improving the fallback is not where effort belongs.
> - §3's leak evaluation **stands and was extended**: managed retention is flat, and native
>   retention is now measured too (`soak`, `leak`, and a unit test), flat at ~8.5 MB.
> - What the profiling did establish that remains useful: clipping was 65% of time and 91% of
>   allocation, `BspNode.Build` degenerates separately on structured input, and two
>   measurement traps (`GC.GetTotalAllocatedBytes(precise: true)` perturbs timing; tiered JIT
>   under-warms small meshes). See the baseline report and `docs/` notes.
>
> **The one thing still worth doing to the BSP kernel** is not performance: it can still die
> with an uncatchable `StackOverflowException`, and it is now reachable automatically as the
> fallback. A stack-probe budget (`RuntimeHelpers.TryEnsureSufficientExecutionStack`) converts
> that into a `Result` failure; a working implementation exists on `feat/exact-kernel`.

**Status:** RESOLVED — superseded by the Manifold integration (`feat/manifold-kernel`).
Retained for the profiling method and the measurement traps it records.
**Original status:** proposal / handoff brief for a dedicated performance session
**Goal:** measure where boolean/heal time and allocations go, check for leaks, and decide
— with evidence — whether to optimise in managed code, parallelise, drop to a native
C/C++ DLL, or move parts to the GPU.

Self-contained: a fresh session should be able to execute from here. Companion to
`docs/proposals/healer-rewrite.md` (correctness); this one is about speed and memory.

---

## 1. Where we are

The benchmark project already exists: `bench/GeometryEngine.Benchmarks` (BenchmarkDotNet, plus
`verify` and `leak` console modes) run against the Fabolus bolus meshes in `bench/files`
(1k–100k triangles, ASCII and binary). See `bench/README.md`.

The mesh-operation pipeline (all **pure managed, single-threaded**):

```
MeshPolygons.From            triangles -> CsgPolygon soup
CsgKernel.{Union,Subtract,Intersect}   BSP boolean; runs on a 256 MB-stack worker thread (DeepStack)
    BspNode.Build / ClippedTo / Inverted / Insert / AllPolygons   recursive, immutable trees
MeshHealer.Build             weld (VertexWelder + spatial hash) -> RepairTJunctions -> CoplanarMerge -> Compact
```

What is already known (this session):

- **No managed leak.** `leak` mode (repeat a boolean, force full GC, compare retained
  managed memory across loop counts) shows a flat retained figure — the engine is
  immutable and holds no static state.
- **Allocation-heavy.** The BSP and healer allocate freely: `ImmutableArray` builders,
  `List<int>`/`List<Vec3>`, `Dictionary`/`HashSet` per node and per plane, `Maybe<T>`
  boxing-free but plentiful, LINQ in a few hot spots (`CoplanarMerge`, `Evaluators`), and
  one 256 MB-stack `Thread` created per boolean call.
- **Fragmentation cost dominated chains** before the coplanar-merge fix (drilling 32
  holes: ~212k triangles in ~37 s). Merge cut that substantially; re-measure now.
- **Degenerate inputs are pathological.** `scalp_bolus` built a BSP deep enough to
  overflow even the large stack until the adaptive tolerance reduced the split count.
- Large meshes (e.g. `test_smoothed_bolus`, ~100k tris) take seconds per boolean.

**None of the real BenchmarkDotNet numbers have been captured yet** — that is step 1.

## 2. Step 1 — baseline and profile (do this before optimising anything)

1. **Capture BDN baselines.** From `bench/GeometryEngine.Benchmarks`:
   `dotnet run -c Release` → `BooleanBenchmarks` (union/subtract/intersect) and
   `ImportBenchmarks`, both with `[MemoryDiagnoser]`. Record Mean, Gen0/1/2, and
   Allocated per op for each mesh. Widen `[Params]` to cover small→large (add a ~100k mesh
   like `test_smoothed_bolus`, mindful of runtime).
2. **See how it scales** with triangle count — is boolean time roughly `O(n log n)`,
   `O(n²)`, dominated by output size (fragmentation)? Plot time and allocated bytes vs
   input/output triangle count.
3. **Find the hot stage.** Split the cost across BSP-build vs clip vs `MeshHealer`
   (weld / RepairTJunctions / CoplanarMerge). Either coarse `Stopwatch` instrumentation
   (temporary, env-gated) or a sampling profiler: `dotnet-trace collect`, `dotnet-counters`
   for GC, or the VS/Rider profiler. Attribute both **time** and **allocations** per stage.
4. **GC pressure.** `dotnet-counters` (gen0/1/2 rate, alloc rate, % time in GC) during a
   batch of booleans. High gen0 churn points at the allocation hot spots to target first.

Deliverable of step 1: a short table of "operation × mesh → time, alloc, dominant stage",
which drives every later decision.

## 3. Step 2 — memory-leak evaluation (managed today, native later)

- **Managed:** extend `leak` mode — longer loops (thousands), track *working set* and
  *gen2 heap* as well as retained managed bytes; confirm the per-boolean `DeepStack`
  `Thread` is joined and not leaking OS threads/handles (watch handle count / thread count
  over a long run). Immutability makes managed leaks unlikely, but large-object-heap
  fragmentation from big `ImmutableArray`s is worth checking.
- **If a native DLL is introduced (§5):** managed leak checks no longer cover it. Native
  allocations must be tracked explicitly — every native handle wrapped in a
  `SafeHandle`/`IDisposable`, every buffer freed. Add a native-memory soak test
  (repeat → measure process working set, expect flat). This is the single biggest risk of
  going native and must be designed in, not bolted on.

## 4. Step 3 — managed optimisation first (cheapest, keeps portability)

Almost always the best ROI, and it preserves the pure-managed, zero-dependency,
cross-platform nature of the library. Likely targets (confirm against the profile):

- **Cut allocations in hot paths:** pool/reuse `List`/`Dictionary`/`HashSet` and builders
  across nodes; remove LINQ from `CoplanarMerge` and `Evaluators` inner loops; prefer
  arrays + `Span<T>`/`stackalloc` (the splitter already uses `stackalloc` for side flags —
  extend the pattern).
- **BSP quality:** improve `ChooseDivider` to avoid near-linear trees on structured input
  (helps `scalp_bolus`); this reduces both depth (stack) and split count (time + output
  size).
- **Curb fragmentation at the source:** the kernel splits along *infinite* planes, so
  faces far from the intersection are cut needlessly (see the coplanar-merge PR). Limiting
  or deferring those splits shrinks the work the healer must undo.
- **DeepStack:** a 256 MB-stack thread per call is heavy if booleans are chained; consider
  reusing one worker or sizing the stack from mesh scale.

Re-baseline after each change with the §2 harness.

## 5. Step 4 — native C/C++ DLL (only if profiling justifies it)

Consider only after managed optimisation plateaus **and** the profile shows a tight
numeric kernel dominating. Two very different routes:

- **(a) Hand-rolled kernel via P/Invoke.** Move the hottest loop (polygon splitting /
  clipping, or vertex welding) to C/C++. Marshal geometry as flat `float`/`double` +
  `int` buffers (pinned or `Span`), cross the boundary as few times as possible (one call
  per operation, not per triangle). Cost: a per-platform native build
  (win/linux/mac × x64/arm64), real unmanaged memory management, marshalling overhead,
  harder debugging, and it breaks the zero-dependency/pure-managed story.

- **(b) Bind a mature native library — recommended if going native at all.** Rather than
  hand-roll, bind an existing, battle-tested CSG/mesh library. **Manifold**
  (github.com/elalish/manifold) is the standout: fast, parallelised, and *guarantees
  manifold (watertight) output* — so it could resolve **both** the performance question
  and the watertightness problem in `docs/proposals/healer-rewrite.md` at once. Others:
  libigl, CGAL, VCGlib, Open CASCADE (heavier). Evaluate Manifold first: prototype a
  P/Invoke binding, run it through `bench verify` (does it produce watertight results with
  matching volumes?) and `BooleanBenchmarks` (how much faster?).

Decision note: binding Manifold is a strategic fork — it would make GeometryEngine a thin managed
layer over a native kernel, trading portability/simplicity for correctness+speed. Worth a
spike and an explicit go/no-go, weighed against finishing the managed healer rewrite.

## 6. Step 5 — GPU / compute shaders (only for a re-architected pipeline)

Be clear-eyed: **BSP CSG is a poor GPU fit** — recursive, branch-heavy, pointer-chasing,
irregular. Porting the current algorithm to a compute shader is not the move. GPUs win on
regular, data-parallel work. So GPU CSG in practice means a *different* algorithm:

- **Voxel / SDF booleans:** sample both solids into a grid or signed-distance field, do
  min/max/subtract per voxel (trivially parallel), then extract a surface (marching
  cubes / dual contouring). Fast and robust-ish, but **approximate** (resolution-limited)
  and changes the output character — may suit previews or very heavy operations, but not
  exact bolus geometry for print without care.
- **Data-parallel helpers on GPU:** vertex transforms, bounds, mass STL rasterisation —
  minor wins, rarely worth the complexity.

Frameworks for C#: ComputeSharp (DirectX compute, Windows), ILGPU (CUDA/OpenCL/CPU,
cross-platform), or raw OpenCL/CUDA bindings. Treat GPU as a **separate exploratory track**
tied to an SDF/voxel pipeline and an accepted approximation, not as an optimisation of the
current exact kernel. Likely out of scope unless approximate results are acceptable for a
specific use case.

## 7. Recommended order (and how to decide)

1. **Baseline + profile** (§2) — never optimise without it.
2. **Managed optimisation** (§4) — biggest ROI, keeps the library portable and dependency-free.
3. **Managed parallelism** — the immutable BSP tree makes parallel subtree clipping safe;
   `CoplanarMerge` is per-plane independent (embarrassingly parallel); batches of booleans
   parallelise trivially. Keep output **deterministic** (parallel results must be
   bit-identical — sort/merge deterministically). Measure overhead vs win.
4. **Native** (§5) — prefer *binding Manifold* over hand-rolling; run a spike through
   `bench verify` + `BooleanBenchmarks`; explicit go/no-go on the portability trade.
5. **GPU** (§6) — only with a re-architected approximate (SDF/voxel) pipeline.

## 8. Constraints and success criteria

- **Correctness must not regress.** Every change re-validated with `bench verify` (volume
  identities + watertightness + defect profiles) and the unit suite (`dotnet run --project
  tests/GeometryEngine.Tests -c Release`, currently 105 green).
- **Determinism.** Output must not depend on thread scheduling or hash order.
- **Zero-dependency ethos** for the library itself (root `NuGet.config` clears sources;
  the benchmark project is the sanctioned exception via `bench/NuGet.config`). Going native
  or adding a GPU framework is a deliberate departure to be justified.
- **Cross-platform.** Pure managed runs everywhere; a native DLL needs per-platform builds
  and packaging.
- **Success:** a documented baseline; leak evaluation (managed, and native if adopted);
  measurable speedups on the `bench/files` set with unchanged correctness; and a written
  go/no-go on native (Manifold) and GPU with evidence.

## 9. Pointers

- `bench/GeometryEngine.Benchmarks/` — `BooleanBenchmarks`, `ImportBenchmarks` (BDN +
  `MemoryDiagnoser`), `Verify.cs`, `LeakCheck.cs`, `TestMeshes.cs`; `bench/README.md`.
- `src/GeometryEngine/Internal/Csg/` — `CsgKernel`, `BspNode`, `CsgPolygon`
  (`PolygonSplitter`), `MeshHealer`, `CoplanarMerge`, `SpatialIndexes` (`VertexWelder`,
  `PointGrid`), `DeepStack`.
- `src/GeometryEngine/Internal/ToleranceStrategies.cs`, `src/GeometryEngine.Core/Geometry/
  IGeometryEngine.cs` (`TopologyValidation`).
- Tooling: `dotnet-trace`, `dotnet-counters`, BenchmarkDotNet `--filter`/`--job`.
- Companion: `docs/proposals/healer-rewrite.md` — note that adopting Manifold (§5b) could
  subsume it.
