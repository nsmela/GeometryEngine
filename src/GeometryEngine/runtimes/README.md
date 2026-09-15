# Native Manifold binaries

Prebuilt binaries of [Manifold](https://github.com/elalish/manifold), the geometry library
backing `ManifoldBooleanOperations`. They are committed rather than restored from a package
feed because the solution root clears all NuGet sources.

## Contents

| file | size | SHA-256 | role |
|---|---|---|---|
| `win-x64/native/manifoldc.dll` | 136,704 B | `88b2a17e98e98f6b6cdc4892a4c1daf94b6b33174b76bae14efd85ddd4babd64` | the C FFI surface (`MANIFOLD_CBIND=ON`), what `DllImport` binds to |
| `win-x64/native/manifold.dll` | 3,126,272 B | `adc3ebb373769d81ca97e12490af91ea2d8538333d9142cc21ef341620287ca6` | the library proper, loaded as a dependency of `manifoldc.dll`; contains oneTBB, linked statically |

Record the checksums when replacing these, so a change of binary is visible in review
rather than being an opaque blob diff.

### geometryengine_native.dll

| file | size | SHA-256 | role |
|---|---|---|---|
| `win-x64/native/geometryengine_native.dll` | 258,048 B | `82781c8213e247d3a62eb70d7dea42c78c3acd3992d915a59b673ba757457fd4` | the libigl distance field behind offsets and batched spatial queries |

Built from this repository's own `native/` (see `native/README.md`), not from upstream: libigl
`v2.5.0` (commit `fdaac01`) and Eigen `3.4.0` (commit `3147391`), header-only, fetched by CMake.
MSVC 19.44 (Visual Studio 2022 17.14), x64, Release, **static CRT** (`/MT`) — it imports nothing
but `KERNEL32`, so unlike `manifold.dll` it needs no VC++ redistributable. It resolves
`manifoldc` by name at runtime rather than linking it. Built 2026-09-15, locally, not by CI.

It is optional: without it `DistanceFieldNative.IsAvailable` is false and offsets and batch
queries run on the managed BVH, slower but with the same results. Offset results record which
field produced them in `MeshMetadata.CreatedBy`.

**win-x64 only.** No Linux, macOS, or Windows-on-ARM binaries ship. On those platforms
`ManifoldNative.IsAvailable` is false and `BspGeometryEngine.Create()` falls back to the
managed BSP kernel, which does **not** guarantee watertight output. Every result records
which kernel produced it in `MeshMetadata.CreatedBy`; see `ManifoldErrors.Unavailable`.

## Licence and attribution

Manifold is licensed under the Apache License 2.0. Copyright The Manifold Authors.

- The full licence text is in `LICENSE.manifold.txt` beside this file, and is copied into
  the build output next to the binaries so it travels with any redistribution, as clause
  4(a) requires.
- Attribution is also recorded in `THIRD-PARTY-NOTICES.md` at the repository root.
- **There is no NOTICE file to propagate.** Apache-2.0 clause 4(d) applies only "If the Work
  includes a 'NOTICE' text file"; upstream Manifold ships a `LICENSE` and no `NOTICE`, so
  there is nothing further to carry. This is recorded explicitly so a later reader does not
  mistake its absence for an omission.
- Manifold's own `LICENSE` is the unmodified Apache-2.0 text with the boilerplate appendix
  left blank; the copyright line above comes from the source file headers.
- **oneTBB carries the same obligation.** It is Apache-2.0 (Copyright © 2005–2025 Intel
  Corporation; Copyright © 2025 UXL Foundation Contributors) and is linked statically into
  `manifold.dll`, so it is redistributed with every copy of these binaries even though no
  file is named after it. Its text is in `LICENSE.oneTBB.txt` beside this file and is
  likewise copied into the build output. Like Manifold, upstream oneTBB ships a `LICENSE`
  and no `NOTICE`, so clause 4(d) again has nothing to propagate.
- **oneTBB's own bundled components go with it.** `LICENSE.oneTBB-third-party-programs.txt`
  is upstream's `third-party-programs.txt`, covering ITT Notify, hwloc, gperftools and three
  smaller pieces. The first three are BSD-3-Clause, and clause 2 there obliges a *binary*
  redistribution to reproduce their notices in the accompanying materials — an obligation
  Apache-2.0 clause 4(a) does not cover, and one that is easy to miss because static linking
  leaves no trace of them on disk. It is carried for that reason rather than out of caution.

## Provenance

These were built from source rather than found, so the configuration below is recorded
rather than recovered.

| field | value |
|---|---|
| Upstream repository | https://github.com/elalish/manifold |
| Commit | `7c86359` — the tip of upstream's `master` on 2026-09-09 |
| Exported `manifold_*` entry points | 293 |
| Toolchain | MSVC 19.44.35228 (Visual Studio 2022 17.14.x), x64 |
| Generator | `Visual Studio 17 2022`, `-A x64` |
| Build type | Release, dynamic CRT (`/MD`) |
| `MANIFOLD_CBIND` | ON |
| `BUILD_SHARED_LIBS` | ON |
| `MANIFOLD_TEST` | OFF |
| `MANIFOLD_PAR` | **ON** — oneTBB v2022.3.0, linked **statically** (`MANIFOLD_USE_BUILTIN_TBB=ON`) |
| Built | 2026-09-11, locally, not by CI |

**`7c86359` is not a tagged release,** and that is deliberate rather than an oversight. The
nearest tag, v3.5.3, is **not an ancestor** of it: the two have diverged, with 46 commits on
`master` that v3.5.3 lacks — among them *Improved decimation* (#1789), the CrossSection
cutover from Clipper2 to boolean2, and a mirror-transform correctness fix (#1784) — against
6 release-branch commits v3.5.3 has and `master` does not. Moving to the tag would therefore
be a change of engine behaviour, not a tidier version string. Under IEC 62304 the honest
record is this table plus the checksums above; the version becomes re-verifiable by name
once upstream tags a release that descends from `master`.

**oneTBB is inside `manifold.dll`.** Static linking means there is no `tbb12.dll` to ship or
to go missing — `manifold.dll` imports nothing beyond `KERNEL32` and the VC++ CRT — but the
code is redistributed all the same, so its licence sits beside Manifold's in
`LICENSE.oneTBB.txt` and both are copied into the build output. This is also why
`manifold.dll` grew from 1.15 MB to 3.13 MB; `manifoldc.dll` is a thin shim and is unchanged
in size.

**They need the Visual C++ redistributable.** The dynamic CRT means a machine without the
VC++ 2015–2022 x64 redistributable cannot load `manifold.dll`, and the library will quietly
fall back to the managed BSP kernel with its weaker guarantee. CI does not catch this — the
GitHub Windows runners ship the redistributable — so it is stated here.
Building with `-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` would remove the dependency.

### Verification run against these exact binaries

- `dotnet run --project tests/GeometryEngine.Tests -c Release` — 117 passed, 0 failed, including
  the determinism test, which is the one a parallel backend is most likely to break.
- `bench verify` — all 15 meshes PASS, union and subtract residuals 0.0000, `kernel` column
  `native` on 14. `mould_test.stl` falls back, exactly as it did with the previous
  single-threaded binaries; it is the one input with a genuine tear.

## Rebuilding

```bash
git clone https://github.com/elalish/manifold
cd manifold && git checkout 7c86359
cmake -B build -G "Visual Studio 17 2022" -A x64 \
  -DMANIFOLD_CBIND=ON -DBUILD_SHARED_LIBS=ON -DMANIFOLD_TEST=OFF \
  -DMANIFOLD_PAR=ON -DMANIFOLD_USE_BUILTIN_TBB=ON
cmake --build build --config Release -j 4
```

Both DLLs land in `build/lib/Release/`. From v3.0 onward, building Manifold as a *static*
library yields a static `manifoldc` rather than a DLL, hence `BUILD_SHARED_LIBS=ON`.
`MANIFOLD_TEST=OFF` skips pulling in gtest, which this build has no use for.

On Windows, clone to a short path. Manifold's build tree plus the fetched oneTBB source
exceeds `MAX_PATH` from a deep working directory, and the failure mode is a `git` or `cl`
error about a filename being too long rather than anything that names the real cause.

Copy the outputs here, then update **the whole provenance table** above, not just the
checksums — a new build invalidates every row of it.

After replacing them, re-run `dotnet run --project tests/GeometryEngine.Tests -c Release` and
`dotnet run -c Release -- verify` from `bench/GeometryEngine.Benchmarks`; the latter's `kernel`
column shows whether the native kernel is actually being used or the fallback has silently
taken over.

## Packaging

Two consumers, two layouts, and the resolver handles both.

**By `ProjectReference`,** the project copies the binaries and the three licence texts flat
into the build output beside the assembly, which is where `ManifoldNative`'s resolver looks
first.

**By package,** `dotnet pack src/GeometryEngine` produces a single `GeometryEngine` package holding
both managed assemblies, the natives under `runtimes/win-x64/native/`, and the licences. The
prediction this section used to make — that the resolver's existing `runtimes/<rid>/native/`
probe would be enough — turned out to be right, and is now verified rather than assumed: a
clean project with one `<PackageReference Include="GeometryEngine" />` produces a result whose
`CreatedBy` reads `GeometryEngine.Booleans.Manifold`, not the BSP fallback.

An earlier attempt also copied the natives flat for package consumers, on the theory that the
SDK would leave them in the NuGet cache. It does not — it copies the `runtimes/` layout into
the output — so that only doubled `manifold.dll` in every consumer's build, and was removed.
`build/GeometryEngine.targets` therefore carries the licences alone, which genuinely are not copied
anywhere by default; see the file for why that matters to anyone redistributing their own
build.
