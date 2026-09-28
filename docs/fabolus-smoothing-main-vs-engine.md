# Fabolus smoothing: main (MeshLib) vs GeometryEngine — 2026-09-27

**Conclusion: GeometryEngine's smoothing reproduces main's geometry closely — mean Dice 0.992,
HD95 0.03–0.23 mm, mean surface separation 0.016–0.069 mm across all eleven inputs, and no case
where the two surfaces disagree by more than 0.23 mm over 95 % of the surface — and it is slightly
*closer* to the unsmoothed anatomy than main is. What does not carry over is topology: three of
eleven outputs were not watertight, against none on main. That has since been **fixed** — the
level-set offset's manifold guarantee held all along, and `MeshCleanup.Weld` was breaking it
afterwards — so all eleven are now watertight and manifold with the geometry unmoved. `larynx small` still
carries thirteen self-intersections 0.06 to 0.49 mm deep, which is what is left and is not
cosmetic.**

Reproduce with `tools/smoothing-comparison` (see its README), which also builds
`fabolus-smoothing-comparison.pdf` at the repository root: the illustrated version of this
document, a page per model with shaded renders of both smoothed meshes, three deviation heatmaps
and that case's metrics. Run on linux-x64, 4 cores, against a
locally built `libmanifoldc.so.3.5.1` at upstream `7c86359` — the commit the shipped win-x64
binaries come from — with `geometryengine_native.so` built from this repository's `native/`.
Both `ManifoldNative.IsAvailable` and `DistanceFieldNative.IsAvailable` were true and every offset
recorded `Offset (native field)` as its producer.

## What was compared

The reference is `nsmela/Fabolus@main`, whose `MarchingCubesSmoothing.Smooth` does:

1. `iterations` x { `OffsetMesh(+deflate, cell)`, `OffsetMesh(-deflate, cell)` } — MeshLib voxel
   offsets at the cell size verbatim.
2. `OffsetMesh(inflate)` — called **without** a cell size, so the voxel size becomes
   `Offset.SuggestVoxelSize(mp, 1e6f)`.
3. `MeshTools.Resize(smoothed, inputTriangles * 2)` — geometry3Sharp `Reducer` with a
   `MeshProjectionTarget`.

The candidate is `SmoothSettings.Apply` on `nsmela/Fabolus@feat/geometry-engine`:
`Modifiers.DoubleOffset(Intensity, Iterations, Resolution)`, `Modifiers.Offset(Inflation,
Resolution)`, `Modifiers.Decimate(inputTriangles * max(RemeshRatio, 1))`.

Both at the UI's standard preset — 1.0 mm offset cycle, 0.1 mm inflation, one iteration, 1.0 mm
cell, 2x triangles — over the eleven inputs in Fabolus's `files/`, each centred on its bounding box
exactly as its own app does on import. The unsmoothed centred mesh was written from the reference
harness, so all three meshes per case share one frame: ICP came back at 0.001–0.037 mm translation
and under 0.18° rotation throughout, which is fitting noise on near-identical surfaces rather than
a frame error, and confirms the harnesses agree about where the model is.

**Component separation was deliberately left off.** `ImportMesh` splits a multi-component file into
separate workspace entries and `main` has nothing equivalent, so splitting one side only would have
compared different geometry. It is reported separately below.

The comparison tool is `tools/Fabolus.MeshCompare` in the Fabolus repository; its `selftest` exits
0 here. A `suite-results` bundle produced alongside it recorded an earlier run of the same tool, but
its head-to-head stage compared `ear_bolus_smoothed.stl` against `test_smoothed_bolus.stl` — two
unrelated files that already shipped smoothed, standing in for branch output that the sandbox could
not generate. Its input audit and its `ear_bolus_smoothed.stl` fidelity baseline are real and are
used below; its `head_to_head` numbers measure nothing about either pipeline and are superseded
entirely by this run.

### The inputs are not all clean

From the tool's own audit, so it is clear which cases were already carrying something:

| input | triangles | watertight | non-manifold edges | components | genus | zero-area faces |
|---|---:|:--|---:|---:|:--|---:|
| `chin_bolus` | 3,216 | yes | 0 | 1 | 0 | 1 |
| `ear_bolus` | 2,812 | **no** | **1** | 1 | not computable | 0 |
| `eye_bolus` | 1,154 | yes | 0 | 1 | 0 | 0 |
| `larynx small` | 2,980 | **no** | **1** | 1 | not computable | 0 |
| `larynx_bolus` | 10,694 | yes | 0 | **2** | 1 | 2 |
| `nose_bolus` | 1,566 | yes | 0 | 1 | 0 | 0 |
| `scalp_bolus` | 8,546 | yes | 0 | 1 | 0 | 1 |
| `small test` | 23,552 | yes | 0 | 1 | 0 | 0 |
| `sphere` | 9,024 | yes | 0 | 1 | 0 | 0 |
| `test bolus 107mL` | 15,212 | yes | 0 | 1 | 0 | 4 |
| `test bolus 7mm` | 10,594 | yes | 0 | 1 | 0 | 3 |

None self-intersects. `ear_bolus` and `larynx small` are the two torn inputs, and `larynx small` is
one of the three cases where the engine's output comes out broken — but `chin_bolus`, which is
clean, is another, so a torn input is not the whole story.

## Read the code first: five differences that predict the numbers

| | main | GeometryEngine |
|---|---|---|
| closing order | dilate then erode | dilate then erode — **same** |
| offset sign | positive outward | positive outward — **same** |
| closing cell | `cell_size` verbatim, 1.0 mm | 1.0 mm **unless the cell budget coarsens it** |
| inflation cell | `SuggestVoxelSize(mp, 1e6f)`, 0.41–1.60 mm here | the same `Resolution`, 1.0 mm, budget-capped |
| decimation | quadric collapse **projected onto the pre-decimation surface** | quadric collapse, **no projection** |

Two of these are the ones worth holding on to.

**The cell budget silently overrides the resolution asked for.** `OffsetHandler` estimates the
samples its padded box would need and, above `NativeCellBudget` (400,000; 50,000 on the managed
field), scales the cell by `cbrt(cells / budget)`. On the five smaller inputs the requested 1.0 mm
survives; on the six largest it does not:

| case | bbox (mm) | engine cell | main closing cell | main inflation cell |
|---|---|---:|---:|---:|
| `chin_bolus` | 94 x 72 x 33 | 1.00 | 1.00 | 0.60 |
| `ear_bolus` | 31 x 73 x 76 | 1.00 | 1.00 | 0.55 |
| `eye_bolus` | 49 x 39 x 38 | 1.00 | 1.00 | 0.41 |
| `larynx small` | 88 x 61 x 76 | 1.09 | 1.00 | 0.73 |
| `larynx_bolus` | 231 x 91 x 99 | 1.82 | 1.00 | 1.26 |
| `nose_bolus` | 52 x 34 x 41 | 1.00 | 1.00 | 0.41 |
| `scalp_bolus` | 152 x 101 x 70 | 1.48 | 1.00 | 1.01 |
| `small test` | 60 x 60 x 40 | 1.00 | 1.00 | 0.52 |
| `sphere` | 160 x 160 x 160 | 2.25 | 1.00 | 1.60 |
| `test bolus 107mL` | 64 x 118 x 90 | 1.27 | 1.00 | 0.87 |
| `test bolus 7mm` | 239 x 140 x 111 | 2.19 | 1.00 | 1.54 |

So on a large model the engine's closing runs on a grid up to 2.25x coarser than main's, which is
where the largest one-sided deviations land — see `test bolus 7mm` below.

**Decimation does not project back.** `MeshDecimator` is Garland–Heckbert quadric collapse and
nothing more; main's `Resize` builds a `DMeshAABBTree3` over the pre-decimation mesh and hands it to
the `Reducer` as a projection target, so surviving vertices are pulled back onto the surface they
came from. This predicts a candidate surface that is slightly rougher for the same triangle count —
and `cand_ref.curvature_ratio` reaches 1.28, with the candidate's area 0.04–2.6 % larger on every
case.

Neither pipeline cares about non-manifold input at the offset: both rebuild the surface from a
sampled field, so `ear_bolus` and `larynx small` (one non-manifold edge each) come out closed on
both sides — main always, the engine with the exceptions below.

## Head to head

`--default-checks` passes on 6 of 11. No case failed on distance or overlap; every failure is
topology, plus one volume and two ICP-noise trips.

| case | Dice | HD95 mm | Hausdorff mm | ASSD mm | signed bias mm | volume % | area % | curvature ratio | checks |
|---|---:|---:|---:|---:|---:|---:|---:|---:|:--|
| `chin_bolus` | 0.9907 | 0.133 | 0.801 | 0.045 | +0.007 | +0.31 | +1.17 | 1.233 | topology |
| `ear_bolus` | 0.9898 | 0.183 | 0.697 | 0.047 | +0.012 | +0.53 | +1.72 | 1.284 | pass |
| `eye_bolus` | 0.9861 | 0.226 | 0.814 | 0.061 | +0.030 | +1.34 | +2.64 | 1.270 | volume |
| `larynx small` | 0.9844 | 0.218 | 1.320 | 0.069 | +0.016 | +0.74 | +2.44 | 1.228 | topology |
| `larynx_bolus` | 0.9961 | 0.121 | 1.228 | 0.032 | +0.002 | +0.04 | +0.64 | 1.029 | topology |
| `nose_bolus` | 0.9857 | 0.200 | 0.639 | 0.067 | +0.012 | +0.51 | +2.34 | 1.228 | pass |
| `scalp_bolus` | 0.9946 | 0.086 | 0.658 | 0.027 | +0.004 | +0.16 | +0.59 | 1.058 | pass |
| `small test` | 0.9988 | 0.037 | 0.056 | 0.022 | +0.022 | +0.24 | +0.19 | 0.810 | self-intersections |
| `sphere` | 0.9997 | 0.034 | 0.081 | 0.016 | +0.015 | +0.06 | +0.04 | 0.963 | pass |
| `test bolus 107mL` | 0.9960 | 0.110 | 0.599 | 0.038 | +0.007 | +0.16 | +0.69 | 0.964 | pass |
| `test bolus 7mm` | 0.9947 | 0.133 | 3.425 | 0.039 | −0.012 | −0.32 | +0.29 | 0.920 | pass |

`surface_dice@0.5` is 0.998–1.000 on every case and `surface_dice@1` never drops below 0.9997: at a
0.5 mm tolerance the two surfaces agree essentially everywhere, and the worst-case figures below
are isolated spots rather than systematic offsets.

### The systematic bias is small and outward

`cand_ref.signed_bias_mm` is positive on ten of eleven cases, mean **+0.010 mm**, never above
+0.030 mm. Volume follows: +0.04 % to +1.34 %, mean +0.34 %, negative only on `test bolus 7mm`.

The code explains the sign. Main's inflation offset runs at 0.41–1.60 mm, *finer* than 1.0 mm on
eight of eleven cases, and a finer grid resolves concavities the engine's coarser grid rounds over,
so the engine's surface sits marginally outside main's. The missing projection target in `Decimate`
pushes the same way: an unprojected quadric collapse keeps the optimal-quadric position rather than
returning to the source surface, which on a convex-dominated bolus lands slightly outside. Both
effects amount to tenths of a percent of volume and hundredths of a millimetre of surface.

The one case that goes the other way is the one the cell budget hits hardest. `test bolus 7mm` is
239 mm across, so the engine coarsens to 2.19 mm against main's 1.00 mm closing, and there
`b_to_a_max_mm` is **3.43 mm** — a feature main resolves and the engine's grid swallows — against
`a_to_b_max_mm` of 0.91 mm. HD95 is 0.133 mm, so this is one localised spot, and it is the failure
mode to watch for on large models. `larynx small` shows the same asymmetry an order of magnitude
smaller (1.32 mm against 1.02 mm).

### Fidelity: the engine is closer to the anatomy

Measured against the centred, unsmoothed input, the engine wins on locality and loses on volume:

| case | Dice main | Dice engine | HD95 main | HD95 engine | volume % main | volume % engine |
|---|---:|---:|---:|---:|---:|---:|
| `chin_bolus` | 0.9773 | **0.9788** | 0.253 | **0.147** | +3.62 | +3.94 |
| `ear_bolus` | 0.9747 | **0.9776** | 0.273 | **0.145** | +3.80 | +4.35 |
| `eye_bolus` | 0.9738 | **0.9773** | 0.284 | **0.146** | +2.84 | +4.21 |
| `larynx small` | 0.9705 | **0.9757** | 0.398 | **0.220** | +3.20 | +3.97 |
| `larynx_bolus` | 0.9850 | **0.9863** | 0.364 | **0.270** | +1.82 | +1.85 |
| `nose_bolus` | 0.9739 | **0.9783** | 0.336 | **0.174** | +3.59 | +4.12 |
| `scalp_bolus` | 0.9778 | **0.9796** | 0.193 | **0.138** | +3.58 | +3.74 |
| `small test` | **0.9963** | 0.9951 | **0.099** | 0.100 | +0.75 | +0.99 |
| `sphere` | **0.9988** | 0.9985 | **0.084** | 0.096 | +0.24 | +0.30 |
| `test bolus 107mL` | 0.9893 | **0.9900** | 0.224 | **0.144** | +1.80 | +1.96 |
| `test bolus 7mm` | 0.9803 | **0.9825** | 0.478 | **0.393** | +2.61 | +2.29 |

HD95 to the input is lower on the engine in nine of eleven cases, typically by a factor of two
(0.179 mm mean against 0.271 mm), and Dice is higher in nine. The two it loses on are `sphere` and
`small test` — the two synthetic shapes, where the pre-decimation surface main projects onto is
itself smooth and regular, so the projection has somewhere good to pull vertices back to. Volume grows more on the engine in ten of eleven, by 0.03–1.37 percentage points;
both pipelines grow the bolus, which is what a closing followed by a 0.1 mm inflation should do.

The earlier chat's baseline stands up as a sanity check: `ear_bolus_smoothed.stl`, shipped in the
repository as a main-produced 2x export, scores Dice 0.975 / HD95 0.27 mm / +3.8 % volume against
`ear_bolus.stl`, and this run's `ear_bolus` reference scores 0.9747 / 0.273 / +3.80 %.

### Topology: the gap, and how it was closed

As first measured, before the fix described below. Every figure in the rest of this document is
from that same run, and the geometry is unaffected by the fix, so they all still stand.

| case | watertight | non-manifold edges | genus | self-intersections |
|---|:--|:--|:--|:--|
| `chin_bolus` | 1 → **0** | 0 → **3** | 0 → not computable | 0 → 0 |
| `larynx small` | 1 → **0** | 0 → **3** | 1 → not computable | 0 → 0 |
| `larynx_bolus` | 1 → **0** | 0 → **1** | 1 → not computable | 0 → 0 |
| `small test` | 1 → 1 | 0 → 0 | 0 → 0 | 0 → **2** |
| other seven | 1 → 1 | 0 → 0 | unchanged | 0 → 0 |

Component counts match everywhere: both pipelines close `larynx_bolus`'s two components into one.
The edge counts are from this run; because the offset is not reproducible run to run (below),
`larynx small` has also come out with one and with two.

**The level-set mesher is not the culprit, and its guarantee holds.** Asked directly, through
`Evaluators.ValidateTopology`, the mesh coming out of `Modifiers.Offset` is closed, manifold,
consistently wound and a single shell on every one of the eleven cases. What it does emit, on
some of them, is a handful of **coincident vertex pairs**: two distinct indices at the same
position, where the offset surface touches itself at a point. That is manifold by the
combinatorial test — every edge still has exactly two faces — and it is a legitimate thing
for a level set to produce. It is a pinch point geometrically.

**`Decimate` is where the pinch becomes a defect.** It welds by position at
`MeshCleanup.RelativeTolerance`, so it fuses each coincident pair into one vertex, and the edges
around the fused vertex then carry four faces. The correlation over the cases is exact:

| case | coincident pairs out of `Offset` | non-manifold edges after `Decimate` |
|---|---:|---:|
| `chin_bolus` | 2 | 3 |
| `larynx small` | 5 | 1 (3 in the run tabulated above) |
| `larynx_bolus` | 1 | 1 |
| `ear_bolus` | 0 | 0 |
| `small test` | 0 | 0 |

The STL export does the same thing independently: the writer casts to `float32`, which lands both
members of a pair on the same bits, and any reader welding on exact coordinates then sees one
vertex. So the defect reaches a slicer whether or not it went through `Decimate` — which is why it
is a real defect in the artefact that matters, not a measurement artefact, even though the mesh
in memory satisfies Manifold's guarantee.

`small test`'s 2 self-intersections are `Decimate`'s too, by a different route: it has no
coincident pairs and is clean before decimation. `MeshDecimator` refuses to collapse a boundary
edge but applies no manifold link condition and no fold test against the rest of the surface, so
a collapse can weld two sheets or push a triangle through one.

The fix belongs in one of two places, and the first is cheaper: weld coincident vertices inside
`Offset` and split the pinch, so the mesh handed on has no two vertices at one position; or give
`MeshDecimator` a link condition, so a weld that would leave an edge with four faces is refused.
Main never shows this because MeshLib's offset does not hand out coincident vertices.

**One case is stranger than the pinch, and unexplained.** `larynx small`'s offset surface is
closed, has no non-manifold edge, and yet has Euler characteristic −5. A closed orientable
surface cannot have an odd Euler characteristic, so something beyond a single pinch is going on
there. Main's output for the same input is Euler 0, genus 1 — one handle, which is what the
input's own topology implies.

### The fix, and what it left behind

`Weld` now returns an already edge-manifold mesh untouched. Such a mesh carries its topology in
its indices, so a weld has nothing to recover there and something to destroy; a triangle soup out
of an STL, where every edge starts with one face, is nowhere near manifold and takes the path it
always did.

That alone does not reach the file. A 32-bit STL has no indices, and the reader recovers topology
by welding on position, so it fuses the pair right back. `Decimate` therefore also eases
coincident vertices apart before handing the mesh on, each moved a hair towards the middle of its
own neighbours and so along its own sheet. The step is four weld tolerances — about a
ten-thousandth of a millimetre on a bolus, several times what a 32-bit float can still tell apart
there, and orders below both the grid the surface was meshed on and anything a printer resolves.

| | before | after |
|---|---|---|
| watertight | 8 of 11 | **11 of 11** |
| non-manifold edges | 3, 3 and 1 | **none** |
| genus | not computable on three cases | computable on all |
| self-intersections | `small test` 2 (micrometres) | `small test` 2, **`larynx small` 13 at 0.06–0.49 mm** |
| `cand_ref.dice` | — | unchanged to four decimal places on all eleven |
| default checks passing | 6 of 11 | 8 of 11 |

**One case got worse, and is the honest remainder.** `larynx small`'s offset surface genuinely
touches itself, and easing the sheets apart leaves them crossing rather than meeting: 13
self-intersections where the fused version reported none. The fused version reported none only
because the counter skips triangle pairs sharing a vertex index, and fusing the pinch gave them
one, so this began as a defect becoming visible rather than a new one.

**They are not cosmetic.** Measured directly, per pair, as the furthest a vertex of one triangle
lies beyond the other's plane: **0.06 to 0.49 mm, median 0.17 mm**. That is printable thickness,
not numerical noise, and `main` has none on the same input. Two other cases are flagged and are
of a different order: `small test`'s two are 0.5 to 1.4 micrometres, and the pinch sites left on
`chin_bolus` and `larynx_bolus` measure at the weld tolerance. `tools/smoothing-comparison/
IntersectionDepth` is what draws that distinction, and it exists because inferring the depth
indirectly — from how far `RepairSelfIntersections` moved the surface — reported nanometres and
was wrong: that repair silently fails to resolve these, so the surface does not move and the
measurement says nothing.

### Removing them, and the measurement problem underneath

`Decimate` is where they are made: it collapses around the pinches and drives one sheet through
the other. Its `SatisfiesLinkCondition` and `FoldsOver` both read only the triangles around the
collapsing edge, so neither can see the sheet it is pushing into.

**Before any of the attempts below can be read, one thing has to be said about the measurement.**
The pipeline is not reproducible run to run (see below), and the crossing count moves with it. At
the fix's own starting point, four runs of the same input at the same settings gave `chin_bolus`
0, 0, 2 and 2 crossings, and `larynx small` 10, 11, 10 and 16. **A single run cannot tell a change
from the noise**, which is how the first pass at this reported a regression on `chin_bolus` that
was the baseline's own spread. Every figure below is from four runs per configuration.

Three approaches were measured. The third is the one that shipped:

| tried | `larynx small` | elsewhere |
|---|---|---|
| **erode on the grid the dilate used** """+EM+""" one cell for both halves of the closing | genus 2 """+EN+"""> 1, crossings down | single-run only; the apparent regressions on `larynx_bolus` and `test bolus 7mm` are not separable from the baseline's spread and are withdrawn |
| **honour the requested cell** """+EM+""" budget raised from 400 k to 5 M so 1.0 mm survives everywhere | 0 crossings | 2.5x runtime, and several cases move further from main; the apparent regressions are single-run and likewise withdrawn |
| **keep the decimator away from the contact** """+EM+""" no collapse touching a pinch or its one-ring in the first pass, a second pass without the guard if the count was not reached | **10"""+EN+"""16 crossings become 0"""+EN+"""4** | only `chin_bolus` and `small test` left with any across the suite, and `small test`'s two are a micrometre |

**What shipped.** Refusing those collapses outright would trade one defect for another, because
reaching the triangle count asked for is the property this decimator was kept for over Manifold's
`Simplify`. So the guard runs in a first pass, and if that pass runs out of edges short of the
count a second re-queues what it held back and collapses without it. Where there is slack """+EM+""" the
smoothing pipeline discards nine triangles in ten """+EM+""" the guard holds and the second pass never
runs; where there is none, the count still lands. All eleven cases come out at exactly the count
they did before, Dice against main is unchanged to four decimal places and HD95 to three, and the
suite costs 17 % more time.

**Two things that did not work**, both measured, both left out. Finding the contacts by proximity
""" + EM + """ asking the tree what lies within a collapse's reach """+EM+""" cannot tell a second sheet from the
same surface curving back on itself; on a bolus, which is nothing but curvature, it pins most of
the mesh and protects nothing in particular. And widening the skirt from one ring to two or three
is worse than one: it holds back so much around each contact that the collapses which do run are
pushed into a different order, and the order is what decides where the surface gets driven
through itself.

**What is still left.** `chin_bolus` keeps two to three crossings at 0.22 to 0.36 mm, against
none to two before, which on this baseline's spread is a small real cost for `larynx small`'s
gain. `small test`'s two remain. Both start at contacts the pinch test does not see, because the
sheets there are near rather than coincident, and closing that gap needs the fold test to consult
the surface rather than the ring """+EM+""" which is the same non-local check this guard is standing in
for.

**The offset is not run-to-run reproducible, and that is the thing to fix first.** Three runs of
`larynx small` at identical settings produced 62,810, 62,826 and 62,792 triangles, and four or
five coincident pairs; the crossing counts above move the same way. The topology verdict was
`nonmanifold=0` every time, so that guarantee is robust even though the tessellation wobbles.
This is Manifold built with the parallel backend, as the shipped win-x64 binary also is.

It means a triangle count or a vertex position is not a safe thing to assert in a test, and that
every comparison in this area needs repeats rather than a single run. It also means the cheapest
way to make everything downstream decidable is to make the offset deterministic — a
single-threaded or order-independent reduction in the level set — because until then each
attempt at the remaining crossings is being judged against a moving target.

Triangle quality holds up but is not identical. No degenerate triangles on either side in any case.
On the seven clinical boli the candidate is modestly more faceted — `aspect_ratio_p95` 3.19
against 3.15 up to 4.86 against 4.16, `min_angle_p5_deg` 1–2 degrees lower, and
`sharp_edge_fraction` 1.1x to 3.5x higher — and on the four larger and synthetic shapes it is
the smoother of the two on all three. That is the same split `curvature_ratio` shows, and the same
cause: no projection target where the cell resolves the detail, a coarser cell where it does not.

### `curvature_ratio` is readable here

Both sides decimate to exactly 2x the input triangle count, and `triangle_ratio` is 1.0000 on nine
cases and 1.0007 on `ear_bolus` and `larynx small`, where MeshLib's loader welds two degenerate
input faces away so main starts from two triangles fewer. The comparison is at matched resolution.
The candidate is rougher on the seven clinical boli (1.03–1.28) and smoother on the synthetic and
largest shapes (0.81–0.96). The
rougher direction is the missing projection target; the smoother direction is the coarser cell,
which cannot represent detail main keeps. Where both effects are small the ratio is near one.

## Runtime

Per case, wall clock, whole pipeline including STL load and save:

| case | input tris | main s | engine s | engine / main |
|---|---:|---:|---:|---:|
| `chin_bolus` | 3,216 | 1.52 | 2.92 | 1.9x |
| `ear_bolus` | 2,810 | 1.02 | 2.09 | 2.0x |
| `eye_bolus` | 1,154 | 0.63 | 0.85 | 1.4x |
| `larynx small` | 2,978 | 0.68 | 2.29 | 3.4x |
| `larynx_bolus` | 10,694 | 1.43 | 2.50 | 1.7x |
| `nose_bolus` | 1,566 | 0.67 | 0.98 | 1.5x |
| `scalp_bolus` | 8,546 | 1.29 | 3.35 | 2.6x |
| `small test` | 23,552 | 0.69 | 1.63 | 2.4x |
| `sphere` | 9,024 | 1.57 | 2.84 | 1.8x |
| `test bolus 107mL` | 15,212 | 0.92 | 2.57 | 2.8x |
| `test bolus 7mm` | 10,594 | 1.15 | 1.80 | 1.6x |
| **total** | | **11.6** | **23.8** | **2.1x** |

The split is the interesting part. The engine's two offset stages cost 0.43–1.91 s and 0.20–1.00 s
against MeshLib's 0.06–0.74 s and 0.10–0.40 s — 1.7–9.0x slower on the closing and
2.1–4.2x on the inflation, on a grid that is equal or coarser. Its `Decimate` runs the other way:
76–668 ms against the `Reducer`'s 201–1050 ms, 1.2–4.7x **faster**, which is the projection
target's cost on main. A 2x whole-pipeline penalty at 1–3 s per bolus is not a usability problem;
the offset stage is nonetheless where any optimisation belongs.

Figures from four cores on linux are not the Windows numbers and should not be quoted as such.

### One trap worth fixing, latent today

The engine's first run here came out 7x slower than the numbers above, on a grid up to 2x coarser,
because `Modifiers.Offset` had silently fallen back to the managed distance field — while
`DistanceFieldNative.IsAvailable` reported true. `native/src/manifold_dynamic.hpp` reaches
`manifoldc` through `dlopen("libmanifoldc.so")` with no path, but the managed resolver loads the
versioned `libmanifoldc.so.3` by absolute path, so the unversioned name is not resolvable through
the loader's own search path and `ge_offset` returns `ManifoldUnavailable`. `Offset` then takes the
managed route, whose cell budget is 50,000 against the native 400,000. On Windows the same shim
calls `GetModuleHandleA("manifoldc.dll")` and finds the module the managed side already loaded, so
the problem cannot arise there — and no non-Windows binaries ship, so nothing is broken today. It
will bite the first person who builds them. Geometry was unaffected, which is worth recording in
its own right: the two field implementations agreed to four decimal places on every distance and
overlap metric, exactly as `native/README.md` claims. Only the topology differed, in a handful of
degenerate spots (`chin_bolus` 2 non-manifold edges against 3, `larynx small` 0 against 3,
`small test` 0 self-intersections against 2).

## Two pipeline differences outside the geometry

**`ImportMesh`'s component splitting turns a stray fragment into a failure.** `larynx_bolus` is one
10,686-triangle bolus plus an 8-triangle speck. Main never splits, closes both into one mesh and
succeeds. With `--separate`, matching what the app actually does, the speck becomes its own
workspace entry, the 1.0 mm erosion consumes it, and `DoubleOffset` returns
`Modifiers.OffsetFailed` — so the user gets an error beside their smoothed bolus. Dropping
components below a triangle or volume floor at import, or reporting a per-entry failure without
surfacing it as the smoothing's failure, would cover it.

**`Modifiers.OffsetSmooth` is not a drop-in replacement, despite what its own docstring
promises.** It is the closing that keeps the grid as its state instead of re-meshing each round,
documented as holding volume near +3.7 % where `DoubleOffset` drifts −1.0 %. Those figures
describe the closing on its own; in this pipeline the 0.1 mm inflation that follows dominates both,
and `DoubleOffset` ends up +0.2 % to +4.4 % against the input rather than negative. Substituted for
`DoubleOffset` at the default preset it **clears every broken-surface defect** — all eight cases
it completes come out watertight, manifold and self-intersection-free, `chin_bolus` included, and
`larynx small`'s excess genus drops from uncomputable to 2 against main's 1. But it
fails outright on the three largest inputs (`larynx_bolus`, `sphere`, `test bolus 7mm`) with
`Modifiers.DistanceBelowCell`: even its larger 1,500,000-cell budget coarsens the cell past 1.0 mm
there, and it refuses a distance below one cell rather than proceeding. Nor is it uniformly faster:
1.4x quicker than `DoubleOffset` on the four smallest cases, and 1.2x to 2.6x *slower* on
`small test`, `test bolus 107mL` and `scalp_bolus`, the three biggest it does complete — that
larger budget is a larger grid to fill. And where it does run it lands much further from main than
`DoubleOffset` does — signed bias +0.081 to +0.137 mm (against +0.002 to +0.030 mm) and volume
+1.1 % to +5.8 % against
main, +1.9 % to +9.6 % against the input. It is a genuinely different answer, and a fatter bolus.
Worth revisiting for its topology if the size limit is lifted and the extra volume is acceptable;
not worth switching to as it stands.

## Where to look next

In the order the data argues for:

1. ~~**`Decimate`'s welding step.**~~ Done: `Weld` leaves an already-manifold mesh alone,
   `Decimate` eases coincident vertices apart before handing the mesh on, and it no longer
   collapses across a contact while it has anywhere else to collapse.
2. **The self-contact in `Offset`.** `larynx small`'s offset surface touches itself, and a finer
   cell does not remove it — measured at the full 1.0 mm rather than the budget's 1.09 mm, the
   coincident pairs are still there. Resolving the contact where it is made is what would clear
   both that case's 13 self-intersections and its excess genus.
3. **Make the offset reproducible.** Every remaining question here — whether a change helped,
   which cases regressed — is being asked of a pipeline whose output moves run to run. Fixing
   that is worth more than any single further heuristic.
4. **A stronger fold test in `MeshDecimator`**, for what the contact guard does not reach:
   `chin_bolus`'s two to three crossings and `small test`'s two, which start where sheets are
   near rather than coincident. `FoldsOver` is local, so a collapse that flips nothing nearby can
   still push a triangle through a distant part of the surface.
5. **The cell budget's silent coarsening.** A caller asking for 1.0 mm gets 2.25 mm on a 160 mm
   model with nothing said, and that is where the single 3.43 mm deviation comes from. Reporting
   the cell actually used in `MeshMetadata` would make it visible; raising the budget would make
   it rarer.
6. **A projection step after decimation**, if the faceting matters. It is the reason the
   candidate's area runs 0.04–2.6 % high and its `sharp_edge_fraction` up to 3.5x main's.
7. **An import floor on stray components**, so an 8-triangle speck does not become a workspace
   entry that cannot be smoothed.

## Suggested thresholds

The defaults are `cand_ref.dice>=0.98`, `hd95_mm<=0.5`, `abs_signed_bias_mm<=0.05`,
`abs_volume_diff_pct<=1`, plus the topology equalities. From eleven cases of data:

| check | default | suggested | why |
|---|---|---|---|
| `cand_ref.dice` | `>=0.98` | `>=0.98` | observed 0.9844–0.9997; the margin is real but thin |
| `cand_ref.hd95_mm` | `<=0.5` | `<=0.30` | observed max 0.226; 0.5 would not have caught anything |
| `cand_ref.assd_mm` | — | `<=0.10` | observed max 0.069, and it is the metric least swayed by one bad spot |
| `cand_ref.abs_signed_bias_mm` | `<=0.05` | `<=0.05` | observed max 0.030; a breach means a grid or projection change |
| `cand_ref.abs_volume_diff_pct` | `<=1` | `<=1.5` | `eye_bolus` at 1.34 % is expected, not a defect |
| `cand_ref.surface_dice@0.5` | — | `>=0.995` | 0.9983–1.0000 here, and it ignores isolated outliers |
| `cand_ref.hausdorff_mm` | — | do not gate | 3.43 mm on `test bolus 7mm` is one grid-resolution spot; report it, do not fail on it |
| `icp.rotation_deg` | `<=0.1` | `<=0.25` | 0.18 ° on `small test` is ICP noise on near-identical surfaces, not misalignment |
| topology equalities | — | keep, all of them | these are the checks that found the real defects |

`cand_ref.curvature_ratio` should stay a reported number rather than a gate: it is only meaningful
at matched triangle counts, and its useful range here (0.81–1.28) is wide enough that any threshold
tight enough to catch a regression would also catch a legitimate cell-size change.
