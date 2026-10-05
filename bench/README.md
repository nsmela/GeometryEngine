# GeometryEngine benchmarks

Timing, correctness and memory checks for the boolean operations, run against real
radiotherapy bolus meshes (the Fabolus v1 test set, committed under `bench/files`).

This is the one project in the repository with an external dependency
([BenchmarkDotNet](https://benchmarkdotnet.org/)). The solution root clears all NuGet
sources to keep the library dependency-free; `bench/NuGet.config` re-adds `nuget.org`
for this project alone, so nothing under `src/` or `tests/` is affected.

## Running

All commands are run from `bench/GeometryEngine.Benchmarks`.

### Verify correctness

```
dotnet run -c Release -- verify
```

Loads every mesh, cuts it with an overlapping sphere, and checks the set-algebra
identities that any correct boolean must satisfy regardless of tessellation:

```
V(A ∪ B) = V(A) + V(B) − V(A ∩ B)
V(A ∖ B) = V(A) − V(A ∩ B)
```

The residuals should be ~0. Watertightness of each result is reported alongside
(`u`/`-`/`n` for union / subtract / intersect). Each mesh is checked in a **child
process** so a mesh that overflows the kernel's stack, or one whose boolean fragments
without bound, is reported as `CRASH` or `TIMEOUT` instead of taking the whole run down.

### Benchmark timing and allocation

```
dotnet run -c Release           # all benchmarks
dotnet run -c Release -- --filter *Subtract*
```

BenchmarkDotNet times import and the three booleans on a representative spread of
meshes and, via its memory diagnoser, reports the managed allocation each makes.

### Inspect input topology

```
dotnet run -c Release -- topology
dotnet run -c Release -- topology-one ear_bolus.stl
```

`topology` tabulates every defect counter for each mesh *as imported*, before any boolean
runs. `topology-one` goes further and names the offending elements — which edge carries too
many faces, which half-edges repeat, which triangles share three vertices — which is what
distinguishes a mesh with a genuine hole from one that is merely carrying a doubled face.

The `verify` run reports the input as one of three states rather than a yes/no, because the
two defects are not comparable: `y` is clean, `dup` is closed but not edge-manifold (a
doubled face or two sheets meeting along an edge — the native kernel accepts these and
cleans them), and `HOLE` is genuinely torn, which the native kernel rejects. Only `HOLE`
makes a mesh unprintable.

### Compare a description with the same steps taken a call at a time

```
dotnet run -c Release -- query
```

Builds a mould with every kind of step in it - a block less the bolus and eight air channels,
joined to four lugs, clipped to a build volume - three ways: one call per step, the batch calls
chained, and one `Solid` description handed to `IBooleans.Evaluate`. Prints the median time,
triangle count, volume and producing kernel for each, so a faster line that built a different
solid cannot pass unnoticed. See `docs/boolean-query.md`.

### Compare an engine that keeps native solids with one that does not

```
dotnet run -c Release -- retain
```

Runs three shapes of work on `SolidRetention.None` and on `SolidRetention.Keep`: one body cut
eight times, a chain of pairwise calls each fed the last one's result, and a description
evaluated again with one channel replaced. Every run starts from meshes the kernel has not
seen. Prints the median time, the volume, and how many solids the engine was keeping when the
run finished. `query` runs with nothing kept, so the two modes measure one thing each.

### Probe for memory leaks

```
dotnet run -c Release -- leak
```

Runs a boolean many times at two loop counts and reports the memory retained after a
full collection. The engine is immutable and stateless, so the retained figure should
be flat across the two runs; growth proportional to the iteration count would indicate
a leak.

## Test meshes

`bench/files` holds the Fabolus v1 `tests/files` STL set (both ASCII and binary). They
range from ~1k to ~90k triangles. The largest, most degenerate meshes currently expose
kernel limitations (unbounded coplanar fragmentation, and BSP recursion deep enough to
overflow even a large stack) — the `verify` run surfaces exactly which.
