# Proposal: rewrite the mesh healer to produce watertight boolean results

> ## SUPERSEDED — 2026-09-10, by the native Manifold kernel (`feat/manifold-kernel`).
>
> This brief set out to make boolean results watertight and printable. **That goal is met, by a
> different route:** Manifold guarantees manifold output by construction, and `bench verify` now
> reports fully watertight results (bnd 0, nm 0, wind 0) on 14 of 15 clinical meshes with exact
> volume residuals. The healer this document proposes rewriting lives in the managed BSP kernel,
> which is now only the fallback — reached by 1 of 15 bench meshes and by platforms without
> native binaries.
>
> Two findings from attempting the rewrite are worth keeping, because both cost real effort to
> establish and both would otherwise be rediscovered:
>
> - **The tolerance was load-bearing for tractability, not just robustness.** An exact-integer
>   kernel was built on a 10 µm lattice (`feat/exact-kernel`) and it fails on curved surfaces:
>   at a fixed radius, 240 input triangles produced 47,376 output and 384 exhausted the stack,
>   while volume identities stayed correct throughout. On a smooth tessellated surface adjacent
>   facets are *nearly* coplanar; the floating-point kernel absorbed them at a single BSP node,
>   and exact arithmetic will not. **Do not pursue exact-arithmetic BSP CSG for tessellated
>   curved input.** The root defect is splitting along *infinite* planes — an
>   intersection-driven boolean has no such failure mode, which is very likely why Manifold does
>   not use a BSP.
> - **One tolerance scalar was serving four unrelated jobs** — plane classification, weld
>   distance, the healer's sliver-area cutoff, and the T-junction deviation limit. The cutoff is
>   *quadratic* in it, so loosening the tolerance 1000× discards 10⁶× more triangle area and
>   punches holes. Loosening it bought up to 13× in speed and destroyed watertightness on every
>   mesh; decoupling the roles was tried and did not rescue it.
>
> Retained for the diagnosis in §1–§3, which remains an accurate account of *why* the managed
> healer could not get there. If the fallback ever needs to produce watertight output — see the
> handover's open question about whether the fallback should exist at all — start here, but
> weigh it against removing the fallback instead.
>
> Related: `docs/manifold-kernel-handover.md`, `docs/proposals/perf-and-native-evaluation.md`
> (also resolved), `docs/manifold-vs-bsp-benchmark.md`.

**Status:** SUPERSEDED — goal met by the Manifold kernel; retained for the diagnosis.
**Original status:** proposal / handoff brief for a dedicated implementation session
**Owner of the problem:** `MeshHealer.RepairTJunctions`
**Goal:** boolean results that are *watertight and printable* on real-world meshes, not just volume-correct.

This document is self-contained: it states the problem, the evidence gathered, why the
current approach fails, a proposed design, and how to validate it. A fresh session
should be able to execute from here.

---

## 1. Background

GeometryEngine is a pure-managed BSP CSG library (`src/GeometryEngine.Core`, `src/GeometryEngine`).
The boolean pipeline is:

```
MeshPolygons.From(mesh)            // triangles -> CsgPolygon soup
CsgKernel.{Union,Subtract,Intersect}   // BSP boolean -> polygon soup (runs on a large stack via DeepStack)
MeshHealer.Build                   // soup -> indexed IMesh
    weld vertices (VertexWelder)
    RepairTJunctions               // <-- the problem
    CoplanarMerge.Merge            // collapse redundant coplanar triangulation
    Compact                        // drop unused vertices, renumber
```

The kernel is correct: across the Fabolus bolus test set (`bench/files`, 15 meshes),
every result satisfies the set-algebra volume identities to ~0 residual
(`V(A∪B)=V(A)+V(B)−V(A∩B)`, `V(A∖B)=V(A)−V(A∩B)`). But the *surfaces* are not
watertight, and a 3D slicer cannot resolve them.

## 2. Evidence (gathered; reproduce with the benchmark harness)

Run `dotnet run -c Release -- verify` from `bench/GeometryEngine.Benchmarks`. It cuts each mesh
with an overlapping sphere and prints a per-result defect profile using
`Evaluators.ValidateTopology` (`TopologyValidation`: boundary/non-manifold/degenerate/
duplicate-vertex/**inconsistent-winding**/**duplicate-face**/**shell** counts).

Representative results (default adaptive-tolerance engine):

```
eye_bolus.stl            u:y -:y n:y   sub[bnd 0  nm 0   wind 0   dup 0  shells 1]   (watertight)
chin_bolus.stl           u:N -:N n:N   sub[bnd 0  nm 12  wind 23  dup 5  shells 3]
small test.stl           u:N -:N n:N   sub[bnd 0  nm 131 wind 314 dup 82 shells 1]
test_smoothed_bolus.stl  u:N -:N n:N   sub[bnd 33 nm 268 wind 587 dup 138 shells 1]
```

Two independent causes were found:

1. **Gaps from too-tight tolerance** — already fixed. `BspGeometryEngine.Create()` now
   uses `AdaptiveTolerance` (scale-relative, factor 1e-7 of the bounding diagonal). This
   made `eye_bolus` and `sphere` fully watertight and stopped `scalp_bolus` overflowing
   the stack. See `Internal/ToleranceStrategies.cs`.

2. **Overlaps from the T-junction repair** — the subject of this proposal. The remaining
   broken results have *low* boundary (naked) edges but *high* inconsistent-winding,
   non-manifold and duplicate-face counts: they are **non-manifold with inverted and
   doubled faces**, not cracked open.

### Localisation (heal-stage toggles, since discarded)

Temporarily gating each heal stage on an env var and measuring `chin_bolus` subtract:

| config              | result                                   |
|---------------------|------------------------------------------|
| weld only           | `bnd 3054, nm 0, wind 0, dup 0`          |
| weld + merge (no repair) | ~same as weld only (merge is innocent) |
| weld + repair (no merge) | `bnd 1, nm 11, wind 22, dup 5`      |
| full                | `bnd 0, nm 12, wind 23, dup 5`           |

**`RepairTJunctions` is the source.** It closes ~3000 T-junction boundary edges but
introduces the non-manifold / inverted / duplicate defects. `CoplanarMerge` is nearly
innocent (it has a strict manifold + boundary-preservation validity check with per-plane
fallback).

### The parameter wall

`PointsOnEdge` accepts a vertex as "on" an edge when its perpendicular deviation is
within the tolerance. Sweeping that deviation threshold (`chin`, `nose`, `small test`):

| deviation factor × tol | effect                                                    |
|------------------------|-----------------------------------------------------------|
| 1.0 (current)          | overlaps present (nm/wind/dup), boundary ~0               |
| 0.1                    | fewer overlaps, some boundary reopens                     |
| 0.01                   | fewer still, more boundary                                |
| 0.001                  | nm/wind/dup → ~0, but boundary large (e.g. small test 477)|

**No single threshold makes these meshes watertight** — it only trades overlaps for
gaps. This is not a mis-tuned parameter; the repair *strategy* is the limitation.

## 3. Why the current strategy fails

`RepairTJunctions` (in `src/GeometryEngine/Internal/Csg/MeshHealer.cs`) processes each
triangle **independently**:

- For each of the triangle's 3 edges, `PointsOnEdge` searches a `PointGrid` for other
  vertices lying within `tolerance` of the edge line (parameter in (0,1)).
- If any are found, the triangle is re-triangulated as a fan from its **centroid** (a
  new hub vertex) around the augmented outline (corners + found points).

Failure modes:

- **Non-conforming neighbours.** Two triangles sharing an edge decide independently
  which points lie on it. Grid queries, the deviation threshold and floating point can
  make them disagree, so a shared edge is split on one side but not the other →
  non-manifold / boundary.
- **False T-junctions.** At the (now scale-relative, ~1e-6) tolerance, dense curved
  meshes have many vertices merely *near* an edge that are not real T-vertices; inserting
  them creates spurious, overlapping micro-triangles.
- **Centroid hubs** add vertices and, combined with the above, produce inverted /
  duplicate faces where fans from neighbouring triangles interfere.

The core defect: the repair is **local and independent**, so it cannot guarantee that
the shared boundary between two faces is triangulated identically from both sides.

## 4. Proposed design

Replace the per-triangle centroid-fan repair with a **conforming, edge-driven** heal
that guarantees both sides of every edge agree by construction.

### Recommended approach — unify T-junction repair with the coplanar merge

`CoplanarMerge` already does the hard part *correctly*: per supporting plane it cancels
interior half-edges, traces the surviving boundary loops, re-triangulates them (ear
clipping with hole bridging), and **accepts the result only if it is a clean manifold
patch whose boundary exactly matches** (else it falls back). It is nearly defect-free in
the measurements.

The insight: a T-junction is just a vertex lying on a boundary loop edge. If, when
tracing a plane's boundary loops, we **insert every mesh vertex that lies on a boundary
edge into that loop** (so the loop passes through it), the subsequent re-triangulation is
automatically conforming with the neighbour — no separate T-junction pass, no centroid
hubs. This turns "weld → repair → merge" into a single conforming per-plane
retriangulation:

```
weld
group triangles by supporting plane                 (CoplanarMerge already does this)
per plane:
    trace boundary loops from surviving half-edges   (already does this)
    for each boundary edge, find welded vertices that lie on it (within welding tol,
        strictly between endpoints) and splice them into the loop, in order
    re-triangulate loops (outer + holes) as today
    accept only if manifold + boundary == expected (existing check) else fall back
Compact
```

Key correctness lever: the "vertices lying on a boundary edge" test uses the **welding
tolerance** (coincidence precision), and the boundary a neighbour presents is the *same*
edge, so both planes splice the *same* welded vertices → conforming by construction.
The existing manifold-and-boundary validity check still guards every plane, so the pass
can only improve or fall back, never corrupt.

### Alternative approach — global half-edge zippering

If unifying with the merge proves awkward:

1. Build a directed half-edge map over the whole welded mesh.
2. A boundary half-edge `a→b` (no opposite `b→a`) that has a welded vertex `m` lying on
   segment `a→b` is a T-junction. Split the face owning `a→b` at `m` (consistent
   orientation), which introduces `a→m` and `m→b`.
3. Iterate to a fixed point (a split can expose further T-junctions).
4. Re-run `CoplanarMerge` to collapse the resulting fans.

This is more mechanical but requires careful orientation handling and a robust
face-split; the unified-merge approach reuses machinery that already has the validity
guard.

### Tolerance

Keep two distinct notions (do **not** reuse one value for both):

- **Welding / coincidence tolerance** — "are these the same point / is this point on
  this edge". Scale-relative (as `AdaptiveTolerance` already provides for booleans).
- The T-junction "on edge" test should use this coincidence tolerance for the
  *perpendicular* distance, because a true cut-point sits on the edge to within welding
  precision.

## 5. Validation plan

Use the existing harness as the before/after metric — this is the whole point of the
`bench` project:

- `dotnet run -c Release -- verify` (from `bench/GeometryEngine.Benchmarks`): volume identities
  must stay ~0; **`IsWatertight` should flip to true for most/all meshes**; the defect
  profile (nm / wind / dup) should drop toward 0. Each mesh runs in a child process, so a
  crash/hang is a reported `CRASH`/`TIMEOUT`, not a dead run.
- `dotnet run -c Release -- leak`: retained memory must stay flat.
- Unit tests: `dotnet run --project tests/GeometryEngine.Tests -c Release` (custom runner;
  currently 105 green). Add regression tests for specific T-junction configurations.

### Must not regress

- The 105 existing tests, including the exact-geometry merge tests and the box/sphere/
  chain watertightness tests.
- **Exact vs float tension:** exact synthetic geometry prefers a tighter tolerance than
  the size-relative default (their needs are disjoint — measured). The 8-hole-plate merge
  test pins a fixed `1e-9` engine for this reason. A healer rewrite should be checked at
  *both* the adaptive default and a fixed tight tolerance.
- Pure-managed, zero-dependency library (root `NuGet.config` clears sources);
  `TreatWarningsAsErrors=true`; `net8.0`; deterministic output (sort by index where hash
  order would otherwise leak in).

### Success criteria

- Most/all `bench/files` meshes report `IsWatertight` after union/subtract/intersect,
  volumes unchanged, defect counts near zero.
- No regression in the unit-test suite or the showcase (`samples/GeometryEngine.Showcase`).

## 6. Pointers

- `src/GeometryEngine/Internal/Csg/MeshHealer.cs` — `Build`, `RepairTJunctions`,
  `PointsOnEdge`, `Emit`, `CellSizeFor`, `Compact`.
- `src/GeometryEngine/Internal/Csg/CoplanarMerge.cs` — boundary-loop extraction, ear
  clipping with hole bridging, the `IsValidPatch` manifold+boundary guard, sliver
  rejection. This is the machinery to extend.
- `src/GeometryEngine/Internal/Csg/SpatialIndexes.cs` — `VertexWelder`, `PointGrid`.
- `src/GeometryEngine.Core/Geometry/IGeometryEngine.cs` — `TopologyValidation` (the metrics).
- `src/GeometryEngine/Internal/ToleranceStrategies.cs` — adaptive vs fixed tolerance.
- `bench/GeometryEngine.Benchmarks/Verify.cs` — the verify harness and defect profile.

### Reproducing the stage localisation

Temporarily gate the stages in `MeshHealer.Build`, e.g.

```csharp
var repaired = Environment.GetEnvironmentVariable("HEAL_NO_REPAIR") is null
    ? RepairTJunctions(vertices, triangles, tolerance) : triangles;
var merged = Environment.GetEnvironmentVariable("HEAL_NO_MERGE") is null
    ? CoplanarMerge.Merge(vertices, repaired, tolerance) : repaired;
```

then run the `verify` harness (or a small scratch program) under each env combination.
Remove the toggles before committing.
