# Handover: native Manifold kernel — what remains

**Date:** 2026-09-10
**Branch:** `feat/manifold-kernel` → PR [#10](https://github.com/nsmela/MeshCSG/pull/10)
**State:** feature-complete and verified; one item blocked on a human, the rest optional.

Self-contained: a fresh session should be able to pick any item below and execute from here.

---

## Where things stand

The native [Manifold](https://github.com/elalish/manifold) library is the default boolean
backend, with the managed BSP kernel retained as a fallback. Verified on this branch:

- **116/116 unit tests green.**
- **`bench verify`:** all 15 clinical meshes satisfy the volume identities; **14 of 15 produce
  fully watertight output** (bnd 0, nm 0, wind 0) with residuals of 0.0000.
- **`bench leak`:** managed retention flat (39 KB across 100 vs 400 iterations), native flat at
  ~8.5 MB.
- **`samples/GeometryEngine.Showcase`:** all 8 models watertight. Worth noting the triangle counts —
  `07-csg-showcase` is a chain of four operations and comes out at 2176 triangles. Under the
  BSP kernel, chained booleans fragmented without bound (drilling 32 holes reached ~212k
  triangles). Manifold removes that failure mode too, not just the slowness.
- **40–142× faster** than the BSP kernel, measured in one job with both backends. See
  `manifold-vs-bsp-benchmark.md`.

Guarantees are explicit rather than assumed: `MeshMetadata.CreatedBy` names the producing
kernel, `verify` has a `kernel` column, and native failures are `Result` values rather than
exceptions.

---

## 1. Binary provenance — BLOCKED ON A HUMAN

**The only item nobody else can do.** The committed DLLs were built without a version
resource, so their origin cannot be recovered from the files.

Fill in the marked fields in `src/GeometryEngine/runtimes/README.md`:

- upstream tag and commit SHA
- toolchain (compiler and version)
- CMake flags — **especially whether TBB was enabled**, which changes both behaviour and
  performance
- who built them, and when

What *is* established: version is **Manifold 3.x**, from the exported symbols
(`manifold_meshgl64_*` and `manifold_get/set_tolerance` are 3.0 additions, 293 `manifold_*`
entry points), and SHA-256 checksums are recorded for both files so a binary swap shows up in
review rather than as an opaque blob diff.

**Why it matters:** for radiotherapy bolus work these are third-party software of unknown
provenance under IEC 62304, where version and build configuration are precisely what must be
documented and re-verified on change. Also, without the tag the binaries cannot be rebuilt or
patched for a security fix.

---

## 2. Stack-overflow guard for the BSP fallback — highest-value engineering item

**What:** the BSP kernel can still die with an uncatchable `StackOverflowException`. `DeepStack`
reserves a 256 MB stack to push the ceiling out, and its own remarks concede the residual risk.

**Why it matters more now than before:** BSP is no longer something a caller opts into — it is
the *automatic fallback*, reached by `mould_test.stl` today and by every operation on a platform
without native binaries. So a crash path is reachable by default, on exactly the awkward input
that triggers a fallback. That breaks the library's contract that every failure is a value.

**How:** don't guess how many frames the stack holds — ask the runtime. A working implementation
is on `feat/exact-kernel` (`src/GeometryEngine/Internal/Csg/CsgBudget.cs`):

```csharp
if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
{
    throw new CsgBudgetExceededException(...);   // caught at CsgKernel's edge → Result
}
```

Called on entering each level of `BspNode.Insert` and `ClipPolygons`, with a generous
cumulative fragment cap as a backstop against an explosion too shallow to threaten the stack.
The exception is internal only; `CsgKernel` converts it to `Result.Failure`.

**Watch out for:** the cumulative fragment counter counts *every* split across the whole
operation, not the output size, so it is far larger than it looks — set it generously or it
will fail legitimate operations. That mistake was made and corrected on that branch.

**Verify with:** `bench verify` (no CRASH rows), and confirm a deliberately pathological case
returns a failure instead of killing the process.

---

## 3. `mould_test.stl` — the one remaining non-watertight output

**What:** 1 of 15 meshes still produces non-watertight output, via the fallback. It is honestly
labelled (`HOLE` input, `FALLBACK` kernel), so nothing is hidden.

**The defect is small and fully diagnosed** (`bench topology-one mould_test.stl`):

- triangle 1953 has **two free edges**: (1007,1008) and (1008,1009)
- edge (1007,1009) is used **3×** by triangles 1953, 1956, 2058 — an *odd* count, so half-edges
  cannot pair, which is exactly why Manifold rejects the mesh

**Options, in increasing ambition:**

1. Leave it. Documented, labelled, one mesh. Reasonable.
2. **Targeted repair** — this is a single localised tear, not general mesh damage. Closing it
   would take the set to 15/15 through the native kernel. Much smaller than a general repair
   stage.
3. General input repair before the kernel. Only worth it if more real inputs turn out damaged.

Note `ear_bolus` and `larynx small` also report a non-clean input (`dup`) but are **closed** —
they need nothing, and Manifold cleans them.

---

## 4. Cross-platform native binaries

Only `win-x64` ships. Everything else silently uses the BSP fallback with its weaker guarantee
(labelled, not hidden). The resolver **already probes** `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64` and `win-arm64` in the NuGet `runtimes/<rid>/native/` layout, so this is a build and
packaging job, not a code change:

```bash
cmake -B build -DMANIFOLD_CBIND=ON -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON
cmake --build build --config Release
```

`BUILD_SHARED_LIBS=ON` matters: from Manifold v3.0 a static build yields a *static* `manifoldc`
rather than a shared library. Do this in CI rather than by hand, and record provenance per
platform (item 1).

---

## 5. NuGet packaging

The project copies the binaries and licence **flat** into the build output, beside the assembly
— which is where the resolver looks first, and it flows correctly through project references
(confirmed: the showcase sample's output contains both DLLs and the licence). A NuGet package
needs the `runtimes/<rid>/native/` layout preserved *inside the package*; the resolver already
probes that layout, so only packaging metadata is missing.

---

## 6. Smaller tidy-ups

- ~~Mark `docs/proposals/healer-rewrite.md` superseded~~ — **done.** Both proposals now carry a
  banner; the healer brief also records the two findings from attempting the exact-kernel
  rewrite, since both cost real effort to establish.
- ~~Split `TopologyValidation.IsClean`~~ — **done.** It is now the conjunction of four named,
  independent questions (`IsClosed`, `IsEdgeManifold`, `IsConsistentlyWound`,
  `HasRedundantGeometry`), unchanged in meaning, with a test pinning the equivalence.
- **`feat/conforming-healer`** has **uncommitted** diagnostic knobs, including an
  `Environment.GetEnvironmentVariable` call in `MeshHealer.Emit` — a *per-triangle* hot path.
  Remove those before ever measuring that branch; they silently invalidate timings.
- **`feat/exact-kernel`** — keep. It is a documented dead end, but it holds the `CsgBudget`
  implementation (item 2), a reusable exact-`int64` predicate layer, and the measurement showing
  why exact-arithmetic BSP cannot work on curved meshes.
- **`meshcsg-source/` and `meshcsg-source.tar.gz`** at the repository root are a duplicate copy
  of the whole project, but they are **untracked and gitignored** — local clutter in one working
  directory, not part of the branch. Only worth knowing because a repo-wide `grep` hits both
  trees and can double every result.

---

## 7. A decision, not a task: should the fallback exist at all?

Worth a deliberate answer rather than drifting into one.

The fallback makes the library work everywhere and accept input Manifold rejects. But it
substitutes a kernel with a **weaker guarantee** — the BSP kernel does not promise watertight
output, and on the meshes Manifold rejects it demonstrably does not deliver it. For a medical
device, quietly producing a best-effort result where a guaranteed one was expected may be worse
than refusing.

The current design mitigates this rather than resolving it: every result records its producing
kernel, so a caller *can* tell — but only if it looks. The alternatives are to fail loudly
instead, or to have the caller opt in to degradation explicitly.

Note the binding-mismatch case already takes the strict line: `Manifold.BindingMismatch` does
**not** fall back, because it means the code does not match the library and substituting a
kernel would hide the bug.

---

## Deliberately not doing

Two validated optimisations are recorded and **intentionally not landed**, because they improve
a path reached by 1 of 15 meshes, against an overall 142×:

- **`ClipPolygons` empty-input early-out** — 2.3× faster, 2.25× less allocation on the 100k
  mesh, byte-identical `verify` profile across all 15 meshes, 6 lines. Patch and measurements in
  `perf-and-native-evaluation.md`.
- **`ChooseDivider` quality fix** — `BspNode.Build` is 68% of a boolean on `small test` versus
  4% on the 4× larger `test_smoothed_bolus`; the 8-candidate sample degenerates on structured
  input.

**Revisit only if** the fallback stops being marginal: if non-Windows becomes a primary target
without native binaries, or if real inputs turn out to be damaged often enough that Manifold
rejects them routinely.

---

## Traps worth knowing before you measure or judge anything here

1. **`GC.GetTotalAllocatedBytes(precise: true)` perturbs timing.** It suspends the runtime;
   calling it between stages inflated a whole boolean by ~2.7×. Measure time and allocation in
   **separate passes**.
2. **Tiered JIT under-warms small meshes.** One warm-up boolean left `eye_bolus` reading 505 ms
   against BenchmarkDotNet's 189 ms, while large meshes agreed within 3%.
3. **BDN's `Allocated` column is managed-only.** For the native kernel it captures just the
   marshalling buffers, not what Manifold allocates internally. The *time* columns are
   comparable; the allocation columns are not. Native retention: use `soak` / `leak`.
4. **Two different manifoldness criteria, both valid.** Ours tests every *undirected* edge for
   exactly two faces; Manifold tests every *directed* half-edge for one opposite partner. A
   coincident oppositely-wound triangle pair passes Manifold's and fails ours. Ask `IsClosed`
   and `IsEdgeManifold` separately — only a hole makes a mesh unprintable.
5. **`bench` pairs every mesh with a small fixed 960-polygon sphere**, so all measured costs
   describe mesh-minus-small-tool. Large-minus-large is unmeasured and would scale differently.

---

## How to verify the branch

```bash
dotnet run --project tests/GeometryEngine.Tests -c Release          # expect 116/116
```

From `bench/GeometryEngine.Benchmarks`:

```bash
dotnet run -c Release -- verify                              # 15 pass; kernel column
dotnet run -c Release -- topology                            # input defects per mesh
dotnet run -c Release -- leak                                # managed + native retention
dotnet run -c Release -- --filter '*BooleanBenchmarks*' --job short   # both kernels
```

From `samples/GeometryEngine.Showcase`: `dotnet run -c Release` — 8 models, all watertight.

To confirm graceful degradation, copy a build output aside, delete `manifold*.dll` and
`runtimes/` from the copy, and run `verify-one` there: it should complete and report `FALLBACK`,
not throw.
