# Third-party notices

GeometryEngine's own code has no external dependencies — the solution root clears every NuGet
source deliberately. Three exceptions are redistributed or consumed, and are recorded here.

---

## Manifold

**Redistributed as native binaries.**

- **Project:** Manifold — https://github.com/elalish/manifold
- **Copyright:** The Manifold Authors
- **Licence:** Apache License 2.0
- **Licence text:** [`src/GeometryEngine/runtimes/LICENSE.manifold.txt`](src/GeometryEngine/runtimes/LICENSE.manifold.txt)
- **Version:** built from commit `7c86359` (see the provenance notes below)
- **Form:** unmodified binaries built from upstream source, `win-x64` only

Manifold provides the boolean kernel behind `BspGeometryEngine.CreateWithManifold()`, which
is what `Create()` returns by default. It guarantees manifold (watertight) output, which the
managed BSP kernel does not.

The binaries are committed under `src/GeometryEngine/runtimes/win-x64/native/` and copied
into the build output alongside the assembly, together with the licence text, so the licence
travels with any redistribution as Apache-2.0 clause 4(a) requires.

Upstream Manifold ships a `LICENSE` and **no** `NOTICE` file. Apache-2.0 clause 4(d) applies
only where the work includes a NOTICE, so there is none to propagate here. Noted explicitly
so its absence is not later read as an oversight.

Provenance — commit, toolchain, CMake flags, build date and the binary checksums — is
documented in [`src/GeometryEngine/runtimes/README.md`](src/GeometryEngine/runtimes/README.md).

No modifications were made to Manifold's source. If that ever changes, Apache-2.0 clause
4(b) requires the modified files to carry prominent notices stating that they changed.

---

## oneTBB (oneAPI Threading Building Blocks)

**Redistributed, statically linked inside `manifold.dll`.**

- **Project:** oneTBB — https://github.com/uxlfoundation/oneTBB
- **Copyright:** © 2005–2025 Intel Corporation; © 2025 UXL Foundation Contributors
- **Licence:** Apache License 2.0
- **Licence text:** [`src/GeometryEngine/runtimes/LICENSE.oneTBB.txt`](src/GeometryEngine/runtimes/LICENSE.oneTBB.txt)
- **Version:** v2022.3.0
- **Form:** built from upstream source as part of Manifold (`MANIFOLD_USE_BUILTIN_TBB=ON`),
  linked statically

Manifold is built with `MANIFOLD_PAR=ON`, which is what makes the boolean kernel run on more
than one core. The parallel backend is oneTBB, and building it with
`MANIFOLD_USE_BUILTIN_TBB=ON` links it *into* `manifold.dll` rather than leaving a
`tbb12.dll` to ship beside it.

That is worth stating plainly, because it is the kind of dependency that goes unrecorded:
nothing on disk is named after oneTBB, `manifold.dll` imports nothing that reveals it, and
no package manifest mentions it — yet every copy of these binaries redistributes it, and
Apache-2.0 clause 4(a) applies to it in its own right. Its licence therefore sits beside
Manifold's in the build output, not merely in this document, since a recipient of the
binaries receives the former and not the latter.

Upstream oneTBB ships a `LICENSE` and **no** `NOTICE` file, so as with Manifold clause 4(d)
has nothing to propagate.

**oneTBB's own bundled components.**
[`src/GeometryEngine/runtimes/LICENSE.oneTBB-third-party-programs.txt`](src/GeometryEngine/runtimes/LICENSE.oneTBB-third-party-programs.txt)
is upstream's `third-party-programs.txt`, covering ITT Notify, hwloc, gperftools, a libstdc++
bug workaround, an ActiveState recipe and doctest. The first three are BSD-3-Clause, whose
clause 2 requires a **binary** redistribution to reproduce their copyright notices "in the
documentation and/or other materials provided with the distribution". Apache-2.0 clause 4(a)
does not discharge that, so the file is carried into the build output alongside the licences.

No modifications were made to oneTBB's source.

---

## BenchmarkDotNet

**Build-time and development only; not redistributed.**

- **Project:** BenchmarkDotNet — https://github.com/dotnet/BenchmarkDotNet
- **Licence:** MIT
- **Used by:** `bench/GeometryEngine.Benchmarks` only

The benchmark project is the single sanctioned exception to the zero-dependency rule and
re-adds `nuget.org` through `bench/NuGet.config`. Nothing under `src/` or `tests/` depends on
it, and it is not part of any shipped artifact.
