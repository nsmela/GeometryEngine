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

The magnitude of every distance comes from libigl's AABB tree; only the *sign* comes from the fast
winding number. Neither of libigl's own `signed_distance` helpers is used:

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

```bash
dotnet run -c Release --project bench/GeometryEngine.Benchmarks -- offset
```

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
