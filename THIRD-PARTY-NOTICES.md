# Third-party notices

Everything GeometryEngine redistributes, consumes, or ported from is recorded here.

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

## libigl and Eigen

**Redistributed as compiled code inside `geometryengine_native.dll`.**

- **Projects:** libigl — https://github.com/libigl/libigl (tag `v2.5.0`); Eigen —
  https://gitlab.com/libeigen/eigen (tag `3.4.0`)
- **Licence:** Mozilla Public License 2.0 for both (libigl is also offered under GPL-3.0; MPL-2.0
  is the one relied on)
- **Form:** header-only, unmodified, compiled into the native distance-field library built from
  `native/`

MPL-2.0 clause 3.2 requires that recipients of the executable form be told how to obtain the
source of the covered files. The source is the upstream repositories at the tags above, which
`native/CMakeLists.txt` pins (`GE_LIBIGL_TAG`, `GE_EIGEN_TAG`).

---

## Clipper2

**Consumed as a NuGet package; redistributed as `Clipper2Lib.dll` in consumers' output.**

- **Project:** Clipper2 — https://github.com/AngusJohnson/Clipper2 (package `Clipper2` 1.5.4)
- **Licence:** Boost Software License 1.0
- **Used for:** planar polygon offsets, path buffering and unions

---

## NetTopologySuite

**Consumed as a NuGet package; redistributed as `NetTopologySuite.dll` in consumers' output.**

- **Project:** NetTopologySuite — https://github.com/NetTopologySuite/NetTopologySuite
  (package `NetTopologySuite` 2.6.0)
- **Licence:** BSD 3-Clause (also offered under the Eclipse Distribution License 1.0)
- **Used for:** concave and convex hulls of a mesh's shadow

---

## geometry3Sharp

**Ported, not redistributed.**

- **Project:** geometry3Sharp — https://github.com/gradientspace/geometry3Sharp
- **Licence:** Boost Software License 1.0
- **Ported:** `CurveResampler.SplitCollapseResample` and `InPlaceIterativeCurveSmooth`, into
  `src/GeometryEngine/Internal/Planar/CurveResampling.cs`, so outlines and painted paths keep the
  shape they had when Fabolus used the package directly.

---

## BenchmarkDotNet

**Build-time and development only; not redistributed.**

- **Project:** BenchmarkDotNet — https://github.com/dotnet/BenchmarkDotNet
- **Licence:** MIT
- **Used by:** `bench/GeometryEngine.Benchmarks` only

Nothing under `src/` or `tests/` depends on it, and it is not part of any shipped artifact.
