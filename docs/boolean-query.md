# Boolean queries: describe first, evaluate once

Branch `feat/boolean-query`. This records the design discussion that led to it, what the branch
implements, what was measured, and what was deliberately left out.

## Summary

- A chain of pairwise booleans hands every intermediate result back as a mesh, and the native
  kernel reads it in again for the next step. Reading a mesh in costs more than the boolean.
- `Solid` describes a boolean tree as a value. `IBooleans.Evaluate(Solid)` runs it in one native
  pass: each distinct mesh is read once, and only the final solid is written out.
- On a 100k-triangle mould, one description takes 451 ms against 2,658 ms for one call per step
  and 658 ms for the existing batch calls chained.
- Nothing native outlives the call, so there are no finalizers and no cached handles.

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
- **Scoped handles instead of a cache.** The first review proposed caching a native handle on
  each mesh. The description removes the need: handles live inside one call.
- **Two interpreters.** The native kernel evaluates the tree in one pass; the managed BSP kernel
  folds the same tree pairwise, and is the fallback for a description Manifold declines.

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

## Measurements

`bench query`: a block less the bolus and eight air channels, joined to four lugs, clipped to a
build volume. Median of five runs after a warm-up. Volumes agree across all three rows per mesh.

| Mesh | One call per step | Batches, chained | One description |
|---|---|---|---|
| `chin_bolus.stl`, 3,216 triangles | 105.7 ms | 36.8 ms | 28.2 ms |
| `small test.stl`, 23,552 | 607.4 ms | 172.4 ms | 120.8 ms |
| `test_smoothed_bolus.stl`, 100,612 | 2,657.7 ms | 658.4 ms | 451.3 ms |

### How far to trust these

- Measured on Linux, .NET 8, one core, against Manifold 3.5.1 built here **without** TBB. The
  shipped win-x64 binary is parallel, and the repository targets .NET 10. Ratios should hold in
  direction; rerun `query` on the target machine for figures.
- The marshalling and phase tables in section 1 come from separate microbenchmarks (a C# copy of
  the marshalling loops, and Manifold 3.2.1 timed from C++ on sphere operands). They are not in
  the repository.
- `QueryCompare.cs` was compiled and run through a stand-alone project, because BenchmarkDotNet
  could not be restored here. The one-line `query` entry in `bench/.../Program.cs` is therefore
  not compiled as part of the full benchmark project.
- The test suite was run on the same Linux setup: 321 passed, 0 failed, native path included.

## How it was built

Test first, one commit per step:

1. `Solid` and a stubbed `Evaluate`, with the tests that say what evaluating must do.
   RED: 301 passed, 18 failed.
2. Both interpreters. GREEN: 319 passed. The leak test was checked by removing the frees.
3. Pairwise calls routed through one-step descriptions. The 297 earlier tests pass unchanged.
4. Early release of handles. RED checked by disabling it: 9 handles held, not 1. GREEN: 321.
5. `query` bench mode and this document.

## Not done

Each of these was discussed and left out on purpose.

- **Zero-copy marshalling.** Pin `ImmutableArray<Vec3>` on the way in; let the kernel write into
  the final `Vec3[]` on the way out, as `Modifiers.Extract` already does. Worth about 7 ms and
  8 MB per 100k-triangle boolean. Add `[StructLayout(LayoutKind.Sequential)]` and a size test to
  `Vec3` first.
- **An `int32` entry in `geometryengine_native`**, to drop the index widening.
- **A handle cache on the mesh**, for reuse across separate descriptions. In the C++ timing, a
  mixed tree fell from 1,156 ms to 841 ms with leaves already imported. It brings native
  lifetimes back, so it should be justified by a caller that needs it.
- **Other node kinds** (transform, simplify, split). A step that leaves Manifold ends a
  description today.
- **`Select`/`SelectMany` in BasicResults**, for query syntax.

## Running

```bash
dotnet run -c Release --project tests/GeometryEngine.Tests                 # whole suite
dotnet run -c Release --project tests/GeometryEngine.Tests -- description  # filter on test name
cd bench/GeometryEngine.Benchmarks && dotnet run -c Release -- query
```
