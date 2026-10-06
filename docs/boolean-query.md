# Boolean queries: describe first, evaluate once, keep what was read

Branch `feat/boolean-query`. This records the design discussion that led to it, what the branch
implements, what was measured, and what was deliberately left out.

## Summary

- A chain of pairwise booleans hands every intermediate result back as a mesh, and the native
  kernel reads it in again for the next step. Reading a mesh in costs more than the boolean.
- `Solid` describes a boolean tree as a value. `IBooleans.Evaluate(Solid)` runs it in one native
  pass: each distinct mesh is read once, and only the final solid is written out.
- On a 100k-triangle mould, one description takes 451 ms against 2,658 ms for one call per step
  and 658 ms for the existing batch calls chained.
- Under `SolidRetention.Keep`, the default, a mesh the kernel has read in and the mesh behind
  each result keep their native solid. Repeated cuts of one body and step-by-step chains then
  take 43-46% less time, for about 212 bytes of native memory per triangle while the mesh lives.
- `Prepare` pays for reading a mesh in ahead of its first use, `Release` lets a kept solid go
  early, and a part of a description can be moved inside the kernel without being read in again.
- The managed topology audit is five times faster (136 ms to 28 ms at 100k triangles) and a
  binary STL of that size reads about a third faster.

```csharp
var mould = engine.Booleans.Evaluate(
    Solid.Of(block).Subtract(bolus).Subtract(channel).Union(lug).Intersect(envelope));
```

## The discussion

### 1. Could vertices be stored at Manifold's level, and would `in`/`ref`/`out` save copies?

Three questions were checked against the code and then measured.

**Storage.** `Vec3` is three doubles, 24 bytes, blittable. That is already the layout of
`MeshGL64.vertProperties` at `numProp = 3`. Changing how `ImmutableMesh` stores vertices gains
nothing. Indices are `int` here and `uint64` in `MeshGL64`; storing `ulong` would double index
memory for every managed algorithm and change `IMesh`, for a saving of about 0.45 ms per 100k
triangles.

**`in`, `ref`, `out`.** Passing an `IMesh` or an `ImmutableArray<Vec3>` copies a reference, not
the vertices. `in Vec3` measured no gain: `Vec3` is a `readonly struct` and its operators inline.
`out` and `ref` also do not compose with `Result<T>` pipelines or lambdas.

**Where copies do happen.** `ToManifold` and `FromManifold` stage every buffer through
intermediate arrays and lists.

| 100k triangles, per call | Today | Direct |
|---|---|---|
| Marshal in, per operand | 2.63 ms, 3.5 MB | 0.45 ms, 2.3 MB (pin vertices, widen indices) |
| Marshal out | 4.18 ms, 8.4 MB | 1.19 ms, 2.3 MB (kernel writes into the final array) |
| Volume loop through `IMesh.TriangleAt` | 0.95 ms | 0.56 ms (spans hoisted once) |
| Same loop, helper not inlined | 0.97 ms by value | 0.95 ms with `in` |

**The larger cost.** Timing Manifold by phase showed that marshalling is the small part:

| One subtraction, 100k triangles per operand | Share |
|---|---|
| Import both operands (`Manifold(MeshGL64)`) | 64% |
| Boolean | 35% |
| Export | 1.5% |

So the cost worth removing is re-importing, not copying.

### 2. Could a builder defer the work and fire once?

Yes, and it maps directly onto the kernel. Manifold's booleans are lazy: composing one records it
and computes nothing until the result is read. A description built in C# therefore becomes a
chain of native handles with one import per mesh and one export.

Design points settled in the discussion:

- **A value, not a mutable builder.** Each call returns a new description. Building cannot fail,
  so only evaluation returns `Result`.
- **BasicResults.** It offers `Map` and `Bind`, which is enough:
  `Read(...).Map(Solid.Of).Map(s => s.Subtract(tool)).Bind(engine.Booleans.Evaluate)`.
  `from ... select` syntax would need `Select`/`SelectMany` added to BasicResults.
- **Scoped handles first.** The first review proposed caching a native handle on each mesh. A
  description needs none: its handles live inside one call. The cache was added afterwards, for
  work a single description cannot cover (section 3).
- **Two interpreters.** The native kernel evaluates the tree in one pass; the managed BSP kernel
  folds the same tree pairwise, and is the fallback for a description Manifold declines.

### 3. Can Manifold's BVH be extracted and reused?

Not extracted. It is `collider_`, a private member of `Manifold::Impl`, absent from both
`manifold.h` and `manifoldc.h` at the shipped commit (7c86359). It is a tree of triangle boxes in
Manifold's internal face order, reached only through internal templates.

It can be reused, because it lives inside the native handle with everything else the import
built. Keeping the handle keeps the BVH. Timed from C++ on a 100k-triangle body:

| Case | Time |
|---|---|
| Import the body | 76.4 ms |
| One cut, body imported each time | 167.5 ms |
| One cut, body handle kept | 81.0 ms |

Two checks decided the shape of the cache:

- Holding a **leaf** from outside does not stop Manifold folding a run of like operations
  (122 ms held against 131 ms not, import excluded). Holding intermediate steps does, which is
  why `Evaluate` releases them early.
- A kept solid costs **212 bytes per triangle** of native memory (20.3 MB at 100k), against
  about 24 for the managed mesh.

### 4. Which ideas from the proposed architecture summary were worth taking?

A summary proposing span-backed storage, a background BVH, recipe builders, a `SpatialMesh` type
and zero-copy previews was reviewed point by point. Most of its reasoning did not fit this
code, but four ideas survived once recast, and each was built, measured and soaked for memory.

**Warm-up, as `IBooleans.Prepare(mesh)`.** The cost worth paying early is the kernel import, not
the BVH build. `Prepare` reads a mesh in and keeps its solid; it is a hint, safe from any
thread, and a failure reports a mesh the kernel will not take. At 100.6k triangles the first cut
takes 180.4 ms as it comes and 87.5 ms prepared; preparing alone takes 91.7 ms.

Reviewing it found a real defect: a mesh prepared on one thread and cut on another before that
finished was read in by both, and eight threads meeting one mesh read it in eight times. A gate
on the mesh's measurements now lets one thread read it in while the others wait and copy.

**Fused transforms, as `Solid.Translate`, `Rotate` and `Scale`.** A moved part is evaluated by
recording the transform on the solid the kernel already holds; Manifold multiplies successive
transforms together and moves the vertices once. The managed kernel applies the same transform
slices. A kept body nudged and cut once:

| Mesh | Moved first, then cut | Moved in the description |
|---|---|---|
| 3.2k triangles | 5.1 ms | 2.7 ms |
| 23.6k | 33.6 ms | 20.8 ms |
| 100.6k | 177.9 ms | 106.2 ms |

With nothing kept the two cost the same (181.1 ms and 191.7 ms at 100.6k).

**Previews, through kept results, and `IBooleans.Release(mesh)`.** A preview is a
sub-description evaluated to look at; the rest is built on its mesh, which never leaves the
kernel. A mould shown half way, 100.6k triangles:

| Route | Time |
|---|---|
| No preview, one description | 294.0 ms |
| Preview, rest built on it, kept | 284.2 ms |
| Preview, rest built on it, read in again | 374.7 ms |
| Preview, whole mould described again, kept | 400.8 ms |

So with solids kept, a preview the rest is built on costs nothing over having had none. The
cost is memory for previews held but not built on, which is what `Release` is for. It empties
the mesh's slot and disposes the solid; the mesh stays usable and is read in again if needed.

**Hot managed loops.** Hoisting spans turned out not to be the gain: warmed, the statistics pass
measured 1.26 ms against 1.22 ms at 100k triangles, because the runtime already sees through
the interface, and that change was not kept. Timing every managed pass showed where the time
was: the topology audit, at 136 ms and 38 MB, cost more than reading the mesh into the kernel.

- Its edge and face counts used three hash tables of tuples. They are now counted in buckets
  keyed by each edge's lower vertex, in flat arrays allocated once.
- Its duplicate-vertex count used the vertex welder, which searched twenty-seven cells per
  vertex. The welder now files vertices in cells twice the tolerance across and searches eight.
  When several kept vertices are within reach it returns the one the old search would have met
  first, so welded meshes keep the indices they always had.

| | Before | After |
|---|---|---|
| Topology audit, 100.6k triangles | 136.4 ms, 37.9 MB | 27.6 ms, 12.5 MB |
| Topology audit, 23.6k | 23.7 ms, 8.9 MB | 5.6 ms, 3.0 MB |
| Topology audit, 3.2k | 5.7 ms, 1.6 MB | 2.8 ms, 0.4 MB |
| Welding 300,000 STL corners | 101 ms | 64 ms |
| Reading a 100k-triangle binary STL | 108 to 125 ms | 69 to 80 ms |

Both rewrites are held to plain reference implementations in the tests, answer for answer, and
each survived four deliberate mutations being caught. A stronger hash for grid cells was tried
and measured no gain, so it was not kept.

**Memory.** `bench retainsoak` prepares, cuts and drops 100k-triangle meshes without ever
forcing a collection:

| Scenario | Solids alive at most | Process growth |
|---|---|---|
| Prepare, cut, drop; size declared to the collector | 3 | 69 MB |
| The same with the declaration disabled | 12 | 259 MB |
| One kept body dragged and cut, 120 rounds | 2 | 4.1 MB |
| Undo stack of ten previews, each keeping its solid | 13 | 276 MB |
| The same, released as they leave the screen | 1 | 44 MB |

No solids were alive after any run, growth did not rise with run length, and the slowest round
of the drag was 122.9 ms against a median of 89.2 ms. The declaration costs one more full
collection a round with no change in round time.

## What the branch implements

| File | Change |
|---|---|
| `Core/Geometry/Solid.cs` | `Solid` (closed: `Leaf`, `Combined`) and `BooleanOp` |
| `Core/Geometry/IGeometryEngine.cs` | `IBooleans.Evaluate(Solid)` |
| `Booleans/EvaluateSlice.cs` | `EvaluateRequest`, `EvaluateHandler`: the managed pairwise fold |
| `Booleans/ManifoldBooleanOperations.cs` | `Evaluate` with whole-description fallback; pairwise calls are one-step descriptions |
| `Booleans/BooleanShared.cs` | `DescribeQuery` naming, `UnknownOperation` error |
| `Internal/SolidWalk.cs` | iterative post-order, leaves, use counts, geometry identity |
| `Internal/Native/ManifoldKernel.cs` | `Evaluate`: the one-pass native interpreter |
| `tests/.../Booleans/SolidQueryTests.cs` | 24 tests |
| `bench/.../QueryCompare.cs` | `query` mode |
| `SolidRetention.cs` | `None` or `Keep`; `BspGeometryEngine.CreateWithManifold(SolidRetention)` |
| `Internal/Native/RetainedSolid.cs` | `SafeHandle` over one kept solid |
| `Core/Geometry/MeshMeasurements.cs` | an opaque slot for it, beside the spatial index |
| `Internal/Native/ManifoldKernel.cs` | `Acquire`, `Produce`, `Keep`; used by `Evaluate`, `Batch`, `Split` |
| `tests/.../Booleans/RetainedSolidTests.cs` | 11 tests |
| `bench/.../RetainCompare.cs` | `retain` mode |
| `Core/Geometry/IGeometryEngine.cs` | `IBooleans.Prepare(IMesh)`, `IBooleans.Release(IMesh)` |
| `Core/Geometry/Solid.cs` | `Transformed` part and `SolidTransform`; `Translate`, `Rotate`, `Scale` |
| `Core/Geometry/MeshMeasurements.cs` | a gate so one thread reads a mesh in while others wait |
| `Evaluators/TopologyTallies.cs` | edge and face counts in buckets |
| `Internal/Csg/SpatialIndexes.cs` | `VertexWelder` searching eight cells |
| `tests/.../Booleans/PrepareTests.cs`, `SolidTransformTests.cs`, `PreviewTests.cs` | 28 tests |
| `tests/.../Evaluators/TopologyTallyTests.cs`, `Csg/VertexWelderTests.cs` | 8 tests |
| `bench/.../RetainSoak.cs` | `retainsoak` mode |

Behaviour:

- **One import per distinct mesh.** A mesh used at several leaves is read once. A
  `WithMetadata` copy counts as the same geometry.
- **Shared parts evaluate once.** Nodes are memoized by reference, so a description extended in
  two directions from a common start computes the start once.
- **Deep chains are safe.** The walk uses an explicit stack; a 100,000-step chain is tested.
- **Empty intermediates are ordinary.** A step that leaves nothing does not fail the steps after
  it. A leaf with no geometry is still refused before anything runs.
- **Failures.** A mesh the kernel will not take is named. An operation that fails is reported
  for the description as a whole, because reading the status at a step would force that step to
  be computed alone.
- **Naming.** One step is named exactly as the pairwise call names it. Longer descriptions are
  named for their first mesh and a count of the others.
- **Pairwise calls.** `Union`, `Subtract` and `Intersect` on the native facade now build a
  one-step description, so there is one way into the kernel and one fallback decision.

### A finding made while implementing

Holding every handle until the call returned made the description slower than the batch calls.
Manifold folds a run of like operations into one, but only through steps it holds the sole
reference to. A handle kept by the caller is a second reference, and the run is then computed a
step at a time.

The kernel now counts the uses of each part and frees a handle as soon as its last use is
composed. Anything still held on any exit is freed in the one `finally`.

| Mould | Handles held to the end | Released at last use |
|---|---|---|
| 3.2k triangles | 57.0 ms | 28.2 ms |
| 23.6k | 287.8 ms | 120.8 ms |
| 100.6k | 1,235.1 ms | 451.3 ms |

### Keeping solids

- **Lifetime is the mesh's.** The solid hangs off the mesh's measurements, so it is shared by
  `WithMetadata` copies and released by the finalizer once the mesh is collected, or earlier by
  `IBooleans.Release`.
- **Operations never touch what is kept.** They take a copy, which in Manifold is a second
  reference to the same immutable solid, and free it like any handle they own.
- **Results are kept too**, so a result used as the next operand never leaves the kernel.
- **The collector is told.** Each kept solid adds memory pressure of 212 bytes per triangle.
- **Carried through a transform only inside a description.** `Solid.Of(mesh).Translate(...)`
  reuses the kept solid; a mesh moved with `engine.Transforms` is read in afresh.
- **Two threads meeting one mesh at once** read it in once: the second waits and copies.
- **`MeshesImported`** now counts what a call read in, not how many distinct meshes it used.
- `Simplify`, `SmoothEdges`, `Extrude`, `LevelSet` and the `Modifiers` batch union do not keep.

Turn it off with `BspGeometryEngine.CreateWithManifold(SolidRetention.None)`.

## Measurements

`bench query`: a block less the bolus and eight air channels, joined to four lugs, clipped to a
build volume. Median of five runs after a warm-up. Volumes agree across all three rows per mesh.

| Mesh | One call per step | Batches, chained | One description |
|---|---|---|---|
| `chin_bolus.stl`, 3,216 triangles | 105.7 ms | 36.8 ms | 28.2 ms |
| `small test.stl`, 23,552 | 607.4 ms | 172.4 ms | 120.8 ms |
| `test_smoothed_bolus.stl`, 100,612 | 2,657.7 ms | 658.4 ms | 451.3 ms |

`bench retain`: the same engine with and without keeping. Every run starts from meshes the
kernel has not seen. The cuts and the chain are one pairwise call per step; the description is
evaluated once untimed, then again with one channel replaced.

| 100,612 triangles | Read in each time | Kept |
|---|---|---|
| One body, eight cuts | 1,392.4 ms | 770.9 ms |
| A chain of nine calls, step by step | 1,589.6 ms | 868.9 ms |
| A description, redone with one channel replaced | 271.0 ms | 195.0 ms |

| 23,552 triangles | Read in each time | Kept |
|---|---|---|
| One body, eight cuts | 318.8 ms | 185.4 ms |
| A chain, step by step | 327.0 ms | 194.9 ms |
| A description, redone | 64.1 ms | 47.7 ms |

| 3,216 triangles | Read in each time | Kept |
|---|---|---|
| One body, eight cuts | 39.9 ms | 24.0 ms |
| A chain, step by step | 51.7 ms | 34.1 ms |
| A description, redone | 12.2 ms | 9.3 ms |

Each run kept 12 to 16 solids while its meshes were referenced. Where the steps are known up
front, one description is still the faster route: the kept chain takes 869 ms for a mould the
description builds in 271 ms with nothing kept.

### How far to trust these

- Measured on Linux, .NET 8, one core, against Manifold 3.5.1 built here **without** TBB. The
  shipped win-x64 binary is parallel, and the repository targets .NET 10. Ratios should hold in
  direction; rerun `query` on the target machine for figures.
- The marshalling and phase tables in section 1 come from separate microbenchmarks (a C# copy of
  the marshalling loops, and Manifold 3.2.1 timed from C++ on sphere operands). They are not in
  the repository.
- BenchmarkDotNet could not be restored here. The whole benchmark project, entry point included,
  compiles against stand-ins for the five BenchmarkDotNet types it uses, and every console mode
  named in this document was run that way. The BenchmarkDotNet suite itself was not run.
- The test suite was run on the same Linux setup: 369 passed, 0 failed, native path included.
- The audit and welder timings are old and new code interleaved in one warmed process. The
  sandbox was noisy between runs (the old audit measured 136 to 179 ms), so trust the ratios
  more than the figures.
- **Thread safety is argued and tested, but only on one core.** Manifold guards a shared leaf
  with a mutex (`CsgLeafNode::GetImpl`), and the test races eight threads through first use and
  32 cuts of one kept mesh. On a single core that interleaves rather than runs in parallel, so
  repeat it on the target machine before relying on it.
- The 212 bytes per triangle is one measurement of heap growth on glibc, at two mesh sizes.

### Still to be settled on the target machine

Everything above was measured on one core. Three questions need a multi-core Windows run:

1. **Do the threading tests hold under real parallelism?** Run the test suite several times.
2. **Do the timings carry over to the TBB build?** Run `query`, `retain` and `retainsoak`.
3. **How wide is the gap between first calls and settled ones?** Run `warmup`, then again with
   `DOTNET_TieredCompilation=0`. On one core here the first read of a 100k-triangle STL took
   416 ms against 66 ms settled, and the first index build 301 ms against 53 ms; with tiering
   off the first read took 106 ms. Several cores should narrow this, and by how much decides
   whether publishing the caller with ReadyToRun is worth doing.

## How it was built

Test first, one commit per step:

1. `Solid` and a stubbed `Evaluate`, with the tests that say what evaluating must do.
   RED: 301 passed, 18 failed.
2. Both interpreters. GREEN: 319 passed. The leak test was checked by removing the frees.
3. Pairwise calls routed through one-step descriptions. The 297 earlier tests pass unchanged.
4. Early release of handles. RED checked by disabling it: 9 handles held, not 1. GREEN: 321.
5. `query` bench mode and this document.
6. `SolidRetention` declared and ignored, with the tests for keeping. RED: 323 passed, 9 failed.
7. `RetainedSolid` and the kernel's acquire and produce paths. GREEN: 332. Checked by removing
   the native free: the leak test reports 117 MB retained against its 64 MB ceiling.
8. `retain` bench mode and section 3 of this document.
9. `Prepare`: RED 336 passed, 3 failed; GREEN 339.
10. Moved parts: RED 340 passed, 11 failed; GREEN 352. A failure-path leak test was checked by
    leaking one moved solid a step: 1.5 GB retained against its 64 MB ceiling.
11. One read-in per mesh across threads: RED on a count of 16 imports where 9 were needed;
    GREEN 353.
12. Previews and `Release`: RED 357 passed, 4 failed; GREEN 361. The release race (six threads
    cutting a mesh while a seventh releases it continuously) passed seven runs in seven.
13. The audit pinned to its definitions, then rewritten: GREEN 365 before and after.
14. The welder pinned to a plain welder, then rewritten: GREEN 369 before and after.

## Not done

Each of these was discussed and left out on purpose.

- **Zero-copy marshalling.** Pin `ImmutableArray<Vec3>` on the way in; let the kernel write into
  the final `Vec3[]` on the way out, as `Modifiers.Extract` already does. Worth about 7 ms and
  8 MB per 100k-triangle boolean. Add `[StructLayout(LayoutKind.Sequential)]` and a size test to
  `Vec3` first.
- **An `int32` entry in `geometryengine_native`**, to drop the index widening.
- **Carrying a kept solid through `engine.Transforms`.** A part moved inside a description
  reuses its solid; a mesh moved with `Transforms.Translate` first is still read in afresh.
- **Manifold's own queries on a kept solid.** `manifold_ray_cast`, `manifold_min_gap` and
  `manifold_winding_number` are exported at the shipped commit. They need a valid manifold and
  offer no closest point, so they would add to `ISpatialIndex`, not replace it.
- **Other node kinds** (simplify, split). A step that leaves Manifold ends a description today.
- **`Select`/`SelectMany` in BasicResults**, for query syntax.
- **The components pass** (9 to 12 ms and 9 MB at 100k triangles) builds a dictionary of lists
  the way the audit did. It is the next managed pass worth the same treatment.

## Running

```bash
dotnet run -c Release --project tests/GeometryEngine.Tests                 # whole suite
dotnet run -c Release --project tests/GeometryEngine.Tests -- description  # filter on test name
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- query
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- retain
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- retainsoak
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- warmup
```
