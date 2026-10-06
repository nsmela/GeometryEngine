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
- An offset-smooth samples its grid only near the surface. On the workstation the sampling is
  3.4 to 5 times faster and the whole operation about 1.5 to 1.8 times, from a grid that is the
  same to the last bit.
- Run three times on the target workstation (6 cores, Windows, .NET 10): the concurrency tests
  passed 240 of 240 isolated runs, and the speedups carried over at the same ratios each time.
  The runs also showed that Manifold's level-set mesher does not list its output the same way
  twice on several cores.

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

### 5. Where does an offset-smooth spend its time?

An offset-smooth is a closing on a sampled distance grid: sample the mesh's signed distance at
every grid node, inflate and deflate on the grid, mesh the zero level. It was the slowest
operation measured, at seconds on one core.

Sampling every node was 62 to 85% of it. Three ways of filling the grid were compared, with the
rest of the pipeline run on each (one core, closing distance 1.5, milliseconds):

| Mesh | Every node, managed | Every node, native field | Near the surface only |
|---|---|---|---|
| `small test`, 23.5k triangles | 1,954 | 2,416 | 454 |
| `test_smoothed_bolus`, 100.6k | 2,390 | 1,983 | 279 |
| `ear_bolus`, 2.8k | 650 | 743 | 163 |

- **The native distance field was not the answer.** Per core it is no faster than the managed
  BVH, and it changes the result slightly, because it takes its sign from a winding number.
- **Most of the sampling is never read.** A closing reads an exact distance only within its
  reach of the surface: the inflation plus a three-cell margin. Beyond that it needs a node's
  side. About a third of the nodes are within reach.

What was built:

- `MeshBvh.TrySignedDistance(point, reach)`: the distance if the surface is within reach, and
  nothing otherwise. The answer is bit-identical to the unbounded query; a far point is
  dismissed at the first few boxes.
- `SignedDistanceGrid.SampleNear`: measures nodes within reach and gives the rest the side of
  the node before them in the row. The first out-of-reach node of a row is asked outright.
- A rule for when this is sound: the mesh must be closed and consistently wound, decided by one
  pass of the edge tally. Otherwise every node is measured as before.
- The closing and the self-intersection count now use the mesh's shared index.

The grid handed to the mesher is the same to the last bit, on every case tested and on any
number of cores. The mesh is the same vertex for vertex **on one core only**; see "What the
second workstation run found" below. `bench smooth` on one core:

| Mesh | Whole closing before | After | Sampling before | After |
|---|---|---|---|---|
| `small test`, 23.5k triangles | 2,360.8 ms | 1,322.1 ms | 1,400.0 ms | 358.6 ms |
| `test_smoothed_bolus`, 100.6k | 2,103.7 ms | 807.1 ms | 1,787.4 ms | 328.4 ms |
| `ear_bolus`, 2.8k | 825.1 ms | 836.4 ms | not sampled near the surface | |

Meshing the level set (335 to 930 ms here, single-threaded) is now the largest phase.

Two limits:

- **Three of the fifteen clinical bench files do not qualify** (`ear_bolus`, `larynx small`,
  `mould_test`). Each has one edge shared by more than two faces. They are closed as before, at
  the old speed. The rule earns its place: sampled near the surface anyway, `larynx small` puts
  one node of 259,740 on the wrong side.
- **A closed mesh that passes through itself is not detected.** Finding that costs more than the
  sampling saves. Such a mesh may close differently where its surfaces disagree about the inside.

**What the second workstation run found.** On twelve threads (commit `2900701`):

| Mesh | Whole closing before | After | Sampling before | After |
|---|---|---|---|---|
| `small test`, 23.5k triangles | 272.1 ms | 179.8 ms (1.51x) | 144.3 ms | 41.5 ms |
| `test_smoothed_bolus`, 100.6k | 316.1 ms | 162.9 ms (1.94x) | 167.5 ms | 33.6 ms |

| Phase after the change, ms | `small test` | `test_smoothed_bolus` | One core to twelve threads |
|---|---|---|---|
| Build the index | 5.4 | 27.1 | no faster |
| Sample near the surface | 41.5 | 33.6 | 7.2x to 8.3x |
| Inflate and deflate on the grid | 37.0 | 22.8 | no faster |
| Mesh the level set | 93.6 | 56.9 | 4.8x to 6.5x |

The inflate-and-deflate and the index build run on one thread, and are now a quarter to a third
of what is left.

A third run at the same commit measured 252.0 to 169.2 ms (1.49x) and 273.9 to 155.7 ms (1.76x),
with sampling at 3.4x and 4.9x. It also showed the whole-closing ratios are flattered: on
`ear_bolus`, where both rows do identical work, the row timed second came out 8% and 18% ahead
in the two runs. The sampling figures are timed separately and agree between runs, so they are
the ones to trust. `bench smooth` now times the two closings turn and turn about.

The run also failed a test, and the failure was the test's. It compared the two closings' meshes
for exact sequence equality, and failed 25 times in 25 on twelve threads and passed 5 in 5
pinned to one core. Sometimes the vertices matched and only the triangles differed.

The cause, read from Manifold's source at the shipped commit, is in the mesher and not in the
sampling. `LevelSet` hands out vertex and triangle numbers from an atomic counter inside a
parallel loop (`sdf.cpp` lines 282 and 362), so they are numbered in the order threads finish.
The sort that follows is a stable sort on a 30-bit Morton code (`sort.cpp` lines 325 and 471),
which leaves vertices that share a code in the order they arrived. One grid can therefore come
back listed two ways. This was already true of every level-set result the engine produces,
`Offset` included; this branch is where it was first compared closely enough to notice.

What the tests now hold, and why:

- **The grid, bit for bit.** It is everything this code decides, and it is the same on any
  number of cores.
- **The meshes' volume and area, to one part in a million, and watertightness.** That does not
  ask the mesher to repeat itself.

Not yet confirmed on the workstation: that two meshings by the *old* sampling also differ from
each other there. `bench smoothsame` reports exactly that, case by case, with whether the
surfaces are the same once order is set aside.

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
| `Internal/Spatial/MeshBvh.cs` | `TrySignedDistance`: a distance only if the surface is within reach |
| `Internal/Smoothing/SignedDistanceGrid.cs` | `SampleNear` |
| `Modifiers/Modifiers.cs` | `OffsetSmoothHandler`: `GridFor`, `CanSampleNearSurface`, `GridSampling` |
| `Internal/TopologyTallies.cs` | moved from `Evaluators/`, now that two slices use it |
| `tests/.../Modifiers/OffsetSmoothSamplingTests.cs` | 9 tests |
| `bench/.../WarmupProfile.cs`, `SmoothProfile.cs` | `warmup`, `smooth` and `smoothsame` modes |

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
- The test suite was run on the same Linux setup: 378 passed, 0 failed, native path included.
- The audit and welder timings are old and new code interleaved in one warmed process. The
  sandbox was noisy between runs (the old audit measured 136 to 179 ms), so trust the ratios
  more than the figures.
- **Thread safety is argued and tested.** Manifold guards a shared leaf with a mutex
  (`CsgLeafNode::GetImpl`). The one-core sandbox could only interleave threads; the workstation
  run below is the evidence under real parallelism.
- The 212 bytes per triangle is one measurement of heap growth on glibc, at two mesh sizes.

### On the target workstation

The branch at `82b7ec2` was run on a Ryzen 5 5600 (6 cores, 12 threads), Windows 10, .NET
10.0.12, against the shipped multi-threaded Manifold binary. It settled the three questions
the one-core sandbox could not.

**Threading holds.** Four full runs of the suite passed (369 tests each). The six tests that
exercise real concurrency passed 120 of 120 isolated runs, with no crash or hang, and 120 of
120 again in a third run at `2900701`.

**The speedups carry over.** Times are 2.5 to 2.9 times lower than on one core and the ratios
held (100,612 triangles):

| | Before | After | Ratio | One-core ratio |
|---|---|---|---|---|
| Mould: one call per step against one description | 954.9 ms | 172.0 ms | 5.55x | 5.89x |
| One body, eight cuts: read in each time against kept | 542.4 ms | 293.8 ms | 1.85x | 1.81x |
| A chain, step by step: read in each time against kept | 623.1 ms | 331.3 ms | 1.88x | 1.83x |
| A description redone with one channel replaced | 105.4 ms | 68.2 ms | 1.55x | 1.39x |
| The first cut: as it comes against prepared | 66.9 ms | 32.0 ms | 2.1x | 2.1x |
| A kept body moved first against moved in the description | 65.1 ms | 38.6 ms | 1.7x | 1.7x |

`retainsoak` showed no solids alive after any run and no growth between run lengths; the undo
stack of ten previews grew the process 256 MB keeping its solids and 50 MB releasing them. Two
figures differ from the one-core table in section 4 and are not explained:

- The prepare-cut-drop run reached 5 solids alive at once (at 120 rounds in the first run, at
  both lengths in the third), where one core saw 3. Growth stayed at 48 to 49 MB.
- Dragging one kept body held 3 solids and grew the process 46 MB, at both 40 and 120 rounds,
  where one core saw 2 solids and 4.1 MB. It does not rise with run length, so it is not a leak,
  but it is eleven times the growth. The rounds themselves were faster (median 38.2 ms, slowest
  49.7 ms).

The preview rows also changed order: describing the whole mould again with solids kept
(132.3 ms) was faster here than building on a preview that is read in again (138.3 ms), the
reverse of one core. The conclusion stands: a preview the rest is built on costs nothing extra
(100.9 ms against 102.5 ms).

**Warm-up is mostly the first call.** On one core the first ten or more calls of an operation
ran slow. Here the second call is at the settled figure for the operations below:

| 100k triangles | Call 1 | Call 2 | Call 1 with tiering off |
|---|---|---|---|
| Read binary STL | 156.6 ms | 39.3 ms | 52.0 ms |
| Build spatial index | 230.3 ms | 29.2 ms | 39.2 ms |
| Subtract a sphere | 163.2 ms | 85.6 ms | 112.2 ms |

So the cost is a one-off of up to about 200 ms the first time each operation is used. First-call
figures vary by up to a third between runs (reading an STL took 156.6 ms in one and 195.5 ms in
the next), so read them as ranges. One operation is an exception: a batch of 1,000 closest-point
queries stayed three to five times slow through its tenth call in both runs.

Tiering off brings the first call most of the way down, which suggests that publishing the
caller with ReadyToRun would too. That is an inference: ReadyToRun itself was not measured.
Turning tiering off is not the fix, since settled times are worse without it (the audit takes
17.9 ms against 12.8 ms).

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
15. `warmup` bench mode, and the workstation run at this commit.
16. Sampling near the surface: RED 370 passed, 7 failed; GREEN 377. Four mutations were each
    caught, one of them by the bit-for-bit comparison of the closed mesh.
17. The second workstation run, at that commit: 376 passed and the bit-for-bit mesh comparison
    failed, on twelve threads only. The comparison was moved to the grid, where it belongs and
    still catches the same mutation: 378 passed here.

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
- **The managed offset fallback** still builds its own index. It only runs where the native
  library is missing, which is not the target platform.
- **A level-set result is not listed the same way twice on several cores.** The surface is the
  same; the numbering is Manifold's and follows its threads. Two ways to make it repeatable,
  neither taken: put the result in a fixed order after meshing, or mesh with
  `manifold_level_set_seq`, which on this workstation would cost five to six times the meshing.
- **The inflate-and-deflate on the grid runs on one thread**, and is now a quarter to a third of
  an offset-smooth on twelve.
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
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- smooth
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- smoothsame
```
