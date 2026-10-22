# geometryengine_native

A small native library giving GeometryEngine a fast, robust signed distance field, built on
[libigl](https://github.com/libigl/libigl). The committed binary is
`src/GeometryEngine/runtimes/win-x64/native/geometryengine_native.dll`; its provenance and
checksum are in `src/GeometryEngine/runtimes/README.md`.

**It is optional.** Everything it does has a managed implementation behind `MeshBvh`, and
`DistanceFieldNative.IsAvailable` is false when the binary is absent, so a build without it is
slower rather than broken.

## What it does

- **`ge_offset`** runs a whole offset behind one call: builds the field, hands it to Manifold's
  level-set mesher, and returns the mesh. Manifold asks for one sample at a time, so answering
  from managed code costs a P/Invoke transition per sample; here the samples never leave native
  code.
- **`ge_field_*`** answer batches of signed-distance, closest-point and ray queries in parallel,
  for the managed `SpatialIndex` and the decal slice.

## How it signs

The magnitude of every distance is the exact distance to the nearest triangle - from libigl's AABB
tree for batch queries, from `DistanceTree` for offsets (below) - and only the *sign* comes from
the fast winding number. Neither of libigl's own `signed_distance` helpers is used:

- `signed_distance_fast_winding_number` scales the distance by the winding number rather than
  merely signing it, so an isosurface asked for at 1 mm lands short of it.
- `signed_distance_pseudonormal` has no consistent inside to consult on a scan with holes or
  self-intersections, which is most of what arrives from a clinic.

## Why it is fast

An offset samples a whole grid, and nearly all of it is far from the surface being meshed. For
those samples the mesher only needs the sign, so `ge_offset` clamps the field to a narrow band
around the offset: the closest-point search is bounded by it (the tree discards everything beyond
without descending), and outside it the winding number runs at a coarser Barnes-Hut accuracy.
Inside the band the values are exact. Measured on the bench meshes, this took a 1 mm offset from
0.3-0.9 s to 0.16-0.26 s with identical output.

The rest of an offset's field is about memory and threads, and none of it changes a result -
each was checked against the mesh it replaced, bit for bit:

- **Offsets query a flat tree.** libigl's AABB tree allocates every node separately and reaches a
  triangle's corners through the face and vertex matrices. `DistanceTree` keeps its nodes in one
  array and copies each triangle's nine coordinates out in leaf order, so a query walks memory
  forwards. It answers only "how far is the nearest triangle within this bound", which is all an
  offset asks, and builds in about a quarter of the time; sampling ran 23-31% faster. Batch
  queries and rays still use libigl's tree, which also returns the closest point and casts rays.
- **Vertices and faces are row-major,** so a vertex's three coordinates share a cache line rather
  than sitting in three columns: 11-18% off the sampling on its own.
- **The two trees are built at once**, the winding number's on a second thread.
- **libigl gets four threads, not one per hardware thread.** It starts a thread per hardware
  thread for every parallel loop in the winding number's build, and nests them; on a 20-thread
  machine the build of an 81k-triangle mesh took 58 ms with all of them, 20 ms with four.

```bash
dotnet run -c Release --project bench/GeometryEngine.Benchmarks -- offset
```

The bench opts its own process out of Windows power throttling. Left alone, Windows slows a
background console process about 5x a few seconds after it starts, and every row after that point
reads slow.

## Why it is repeatable

A consumer replaying a history from its base mesh has to get back the body it saved, so the same
field has to give the same mesh on every call. Upstream Manifold's parallel mesher does not: it
numbers vertices in whatever order its threads reach them, and its topology cleanup then
resolves some offsets into different geometry as well.

The `manifold.dll` shipped here is built with a patch that fixes that at the source (see
`src/GeometryEngine/runtimes/README.md`): once the parallel passes have made the vertices and
triangles, it renumbers them by where they were made before anything else runs. Patched, the
parallel and sequential meshers give the same mesh as each other, every call, so `ge_offset`
and the managed fallback (`ManifoldKernel.LevelSet`) use the parallel `manifold_level_set`.

Before the patch, `ge_offset` used the sequential mesher behind a parallel pre-sampled lattice:
0.37-0.49 s per bench offset. Now, with the field changes above, 0.17-0.27 s - back to, and below,
what the unrepeatable parallel mesher cost - and a Fabolus smooth (offsets of +1, -1 and +0.1 mm,
then a decimate) runs 50-59% faster. The repeat tests in `Modifiers / offset` fail if an unpatched
`manifold.dll` is put back.

## Building

Both dependencies are header-only and fetched by CMake at pinned tags.

```powershell
cmake -B C:\tmp\ge-native -S native -G "Visual Studio 17 2022" -A x64
cmake --build C:\tmp\ge-native --config Release
copy C:\tmp\ge-native\Release\geometryengine_native.dll src\GeometryEngine\runtimes\win-x64\native\
```

Build in a short path: libigl's fetched tree exceeds `MAX_PATH` from a deep working directory.
Then update the checksum in `src/GeometryEngine/runtimes/README.md` and re-run the test suite.

`manifoldc` is **not** linked; it is resolved by name at runtime (`src/manifold_dynamic.hpp`), so
no import library is needed.

## Versioning

`GEOMETRYENGINE_NATIVE_ABI_VERSION` is bumped whenever `include/geometryengine_native.h` changes
shape. The managed binding checks it on load and treats a mismatch as absent.

## Licences

libigl and Eigen are MPL-2.0; see `THIRD-PARTY-NOTICES.md`.
