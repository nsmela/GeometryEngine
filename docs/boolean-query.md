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
  `WithMetadata` copies and released by the finalizer once the mesh is collected. There is no
  explicit release.
- **Operations never touch what is kept.** They take a copy, which in Manifold is a second
  reference to the same immutable solid, and free it like any handle they own.
- **Results are kept too**, so a result used as the next operand never leaves the kernel.
- **The collector is told.** Each kept solid adds memory pressure of 212 bytes per triangle.
- **Not carried through a transform.** A moved mesh is read in afresh.
- **Two threads reading one mesh in at once** each build a solid; the first is kept and the
  other released.
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
- `QueryCompare.cs` and `RetainCompare.cs` were compiled and run through a stand-alone project,
  because BenchmarkDotNet could not be restored here. The `query` and `retain` entries in
  `bench/.../Program.cs` are therefore not compiled as part of the full benchmark project.
- The test suite was run on the same Linux setup: 332 passed, 0 failed, native path included.
- **Thread safety is argued and tested, but only on one core.** Manifold guards a shared leaf
  with a mutex (`CsgLeafNode::GetImpl`), and the test races eight threads through first use and
  32 cuts of one kept mesh. On a single core that interleaves rather than runs in parallel, so
  repeat it on the target machine before relying on it.
- The 212 bytes per triangle is one measurement of heap growth on glibc, at two mesh sizes.

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

## Not done

Each of these was discussed and left out on purpose.

- **Zero-copy marshalling.** Pin `ImmutableArray<Vec3>` on the way in; let the kernel write into
  the final `Vec3[]` on the way out, as `Modifiers.Extract` already does. Worth about 7 ms and
  8 MB per 100k-triangle boolean. Add `[StructLayout(LayoutKind.Sequential)]` and a size test to
  `Vec3` first.
- **An `int32` entry in `geometryengine_native`**, to drop the index widening.
- **Carrying a kept solid through a transform.** Manifold moves its BVH with the solid, so
  `manifold_transform` on a kept handle would save the re-import after `Translate` or `Rotate`.
- **Manifold's own queries on a kept solid.** `manifold_ray_cast`, `manifold_min_gap` and
  `manifold_winding_number` are exported at the shipped commit. They need a valid manifold and
  offer no closest point, so they would add to `ISpatialIndex`, not replace it.
- **A bound or an explicit release** for kept solids. Today the only limits are the mesh's
  lifetime and the memory pressure declared to the collector.
- **Other node kinds** (transform, simplify, split). A step that leaves Manifold ends a
  description today.
- **`Select`/`SelectMany` in BasicResults**, for query syntax.

## Running

```bash
dotnet run -c Release --project tests/GeometryEngine.Tests                 # whole suite
dotnet run -c Release --project tests/GeometryEngine.Tests -- description  # filter on test name
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- query
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- retain
```
