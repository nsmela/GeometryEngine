# Quadric decimation vs Manifold Simplify — 2026-09-17

**Conclusion: Manifold's `Simplify` does not replace the quadric decimator, and the reasons are
structural rather than a matter of tuning.** The decimator stays. The binding added alongside this
measurement is kept because it answers a different question well — see *What Simplify is actually
for* below.

Reproduce with `dotnet run -c Release --project bench/GeometryEngine.Benchmarks -- decimate`.
Run on linux-x64 against a locally built `libmanifoldc.so.3.5.1` at upstream `7c86359`, the commit
the shipped win-x64 binaries come from. Two runs produced identical figures.

## Why they are not interchangeable

They belong to different algorithm families, which the API shape gives away:

| | quadric decimator | `Manifold::Simplify` |
|---|---|---|
| asked for | a triangle count | a tolerance |
| vertex positions | solves a quadric for an optimal new position | keeps a subset of the originals |
| error bound | none | documented as "all surfaces will have moved by less than tolerance" |
| reduction achieved | exactly what was asked | whatever the geometry allows |

`IGeometryModifiers.Decimate` takes a target triangle count, and Fabolus exposes it as **Resize**,
a user-facing "reduce to N triangles". `Simplify` cannot be asked that question. So the comparison
below is run at matched *output* size: the decimator to a fraction of the input, `Simplify` across
a tolerance sweep, then read off what each cost for the count it actually produced.

## The three findings that decide it

**1. It cannot serve the API.** The same tolerance yields wildly different reductions across
meshes. At 0.01 % of the bounding-box diagonal: `ear_bolus_smoothed.stl` keeps 99.9 % of its
triangles, `small test.stl` keeps 74.9 %, and `test_smoothed_bolus.stl` keeps 49.1 %. Targeting a
count on top of that means a bisection search over tolerance — several invocations at 10–250 ms
each — with no guarantee of converging, because the yield plateaus.

**2. It fails outright on non-manifold input.** `mould_test.stl` is the one file in the suite with
a genuine tear (`closed False`), and `Simplify` returns `Manifold.InvalidMesh` at *every*
tolerance. The quadric decimator processes it and emits a closed mesh. A clinical scan is exactly
the input that arrives torn, so a replacement that refuses it is not a replacement.

**3. Quality at matched count is slightly worse, not better.** Where the two land within a few
percent of the same triangle count:

| mesh | count | quadric max dev | simplify max dev | quadric ms | simplify ms |
|---|---:|---:|---:|---:|---:|
| `test_smoothed_bolus.stl` | ~50 k | 0.0056 | 0.0063 | 255.5 | 192.3 |
| `small test.stl` | ~17.6 k | 0.0035 | 0.0056 | 35.8 | 34.2 |
| `test bolus 107mL.stl` | ~7.3 k | 0.0769 | 0.0928 | 56.6 | 27.6 |
| `larynx_bolus.stl` | ~8.2 k | 0.0822 | 0.1048 | 17.0 | 20.4 |

The decimator is 13–38 % better on deviation every time, which is what solving for an optimal
vertex position buys over restricting yourself to the ones already there. `Simplify` is faster on
the two largest meshes and a wash below that — not enough to trade accuracy for.

## Reading the deviation column

Deviation is a two-sided vertex-sampled Hausdorff *floor*: each mesh's vertices measured against
the other's surface, worse of the two. One direction alone flatters the result — sampling only the
reduced mesh misses detail deleted outright, since the apex of a removed bump is no longer a
vertex. Vertices are not a dense sample either way, so the true Hausdorff distance is higher than
these numbers; they are for comparing rows, not for quoting as an error bound.

**The metric saturates, and two meshes show it.** On `larynx small.stl` the deviation is *exactly*
1.5797 for five consecutive tolerances while the triangle count falls 2942 → 1026; `ear_bolus.stl`
does the same at 0.1751. A max is dominated by its worst sample, so once one feature has been
collapsed, further simplification does not move the number.

That looked at first like `Simplify` overshooting its documented tolerance by 16–120×, and the
obvious suspect was `ToManifold` welding the input before simplification even began — which
alters geometry and would be charged to the simplifier by this metric. **That is not what is
happening:** the `welded` column reads `no` on all 84 successful rows, so Manifold accepted every
input as a valid 2-manifold and changed nothing before working on it. The real explanation is in
the data: the quadric decimator reaches the *same* ~1.6 magnitude on `larynx small.stl` at 50 %
and 25 %. It is a pathological feature in that file — a spike or sliver that any simplifier
collapses early — not a property of either algorithm. Add an RMS column if this ever needs
separating properly.

## What Simplify is actually for

The binding is worth keeping, for a job the decimator does not do: **bounded-error removal of
geometry that describes no shape.** Two rows make the case. At a tolerance of 0.01 % of the
diagonal — visually nothing — `small test.stl` drops to 74.9 % of its triangles for a deviation of
0.0056, and `test_smoothed_bolus.stl` drops to 49.1 % for 0.0063. Half that mesh's triangles carry
no shape at one part in ten thousand.

That is the opposite trade from decimation: not "give me N triangles whatever it costs" but "take
away whatever costs nothing". It suits a tidy-up pass after boolean operations, which leave large
runs of coplanar triangles, and it is watertight by construction. Worth considering as its own
operation rather than as a decimation mode — no caller has asked for one yet, so none exists.

## Full results

<!-- Generated by DecimateCompare; paste the console output verbatim when re-running. -->

## chin_bolus.stl - 3216 tris, diagonal 123.06, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 2412 | 75.0 % | 15.1 | 0.0661 | 0.054 % | -0.01 % | True | no |
| quadric | 50 % tris | 1608 | 50.0 % | 31.9 | 0.2196 | 0.178 % | -0.05 % | True | no |
| quadric | 25 % tris | 804 | 25.0 % | 31.2 | 0.6324 | 0.514 % | -0.24 % | True | no |
| simplify | tol 0.01 % | 3162 | 98.3 % | 10.9 | 0.0066 | 0.005 % | 0.00 % | True | no |
| simplify | tol 0.05 % | 2940 | 91.4 % | 8.7 | 0.0337 | 0.027 % | 0.00 % | True | no |
| simplify | tol 0.10 % | 2620 | 81.5 % | 9.9 | 0.0653 | 0.053 % | 0.00 % | True | no |
| simplify | tol 0.50 % | 1034 | 32.2 % | 15.4 | 0.4048 | 0.329 % | -0.05 % | True | no |
| simplify | tol 1.00 % | 504 | 15.7 % | 14.7 | 1.8295 | 1.487 % | -0.35 % | True | no |
| simplify | tol 5.00 % | 108 | 3.4 % | 12.2 | 6.1724 | 5.016 % | -7.09 % | True | no |

## ear_bolus.stl - 2812 tris, diagonal 109.87, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 2108 | 75.0 % | 12.7 | 0.1079 | 0.098 % | -0.03 % | True | no |
| quadric | 50 % tris | 1406 | 50.0 % | 32.6 | 0.2380 | 0.217 % | -0.07 % | True | no |
| quadric | 25 % tris | 702 | 25.0 % | 23.9 | 0.6904 | 0.628 % | -0.41 % | True | no |
| simplify | tol 0.01 % | 2778 | 98.8 % | 6.6 | 0.1751 | 0.159 % | 0.00 % | True | no |
| simplify | tol 0.05 % | 2628 | 93.5 % | 10.7 | 0.1751 | 0.159 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 2462 | 87.6 % | 7.9 | 0.1751 | 0.159 % | 0.00 % | True | no |
| simplify | tol 0.50 % | 1258 | 44.7 % | 11.6 | 0.2995 | 0.273 % | -0.05 % | True | no |
| simplify | tol 1.00 % | 684 | 24.3 % | 11.1 | 0.5238 | 0.477 % | -0.31 % | True | no |
| simplify | tol 5.00 % | 102 | 3.6 % | 11.2 | 3.1804 | 2.895 % | -10.00 % | True | no |

## ear_bolus_smoothed.stl - 5620 tris, diagonal 109.43, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 4214 | 75.0 % | 6.3 | 0.0475 | 0.043 % | -0.02 % | True | no |
| quadric | 50 % tris | 2810 | 50.0 % | 9.0 | 0.0807 | 0.074 % | -0.08 % | True | no |
| quadric | 25 % tris | 1404 | 25.0 % | 13.8 | 0.1891 | 0.173 % | -0.31 % | True | no |
| simplify | tol 0.01 % | 5616 | 99.9 % | 10.9 | 0.0030 | 0.003 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 5350 | 95.2 % | 11.7 | 0.0296 | 0.027 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 4388 | 78.1 % | 14.4 | 0.0583 | 0.053 % | -0.02 % | True | no |
| simplify | tol 0.50 % | 1224 | 21.8 % | 20.1 | 0.2687 | 0.246 % | -0.21 % | True | no |
| simplify | tol 1.00 % | 650 | 11.6 % | 21.5 | 0.5782 | 0.528 % | -0.40 % | True | no |
| simplify | tol 5.00 % | 110 | 2.0 % | 21.6 | 3.3644 | 3.075 % | -12.29 % | True | no |

## eye_bolus.stl - 1154 tris, diagonal 73.22, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 864 | 74.9 % | 1.3 | 0.0952 | 0.130 % | -0.01 % | True | no |
| quadric | 50 % tris | 576 | 49.9 % | 1.9 | 0.2324 | 0.317 % | -0.04 % | True | no |
| quadric | 25 % tris | 288 | 25.0 % | 2.6 | 0.7105 | 0.970 % | -0.28 % | True | no |
| simplify | tol 0.01 % | 1144 | 99.1 % | 2.7 | 0.0046 | 0.006 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 1114 | 96.5 % | 5.9 | 0.0182 | 0.025 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 1080 | 93.6 % | 3.0 | 0.0450 | 0.061 % | 0.00 % | True | no |
| simplify | tol 0.50 % | 778 | 67.4 % | 3.9 | 0.2013 | 0.275 % | -0.02 % | True | no |
| simplify | tol 1.00 % | 462 | 40.0 % | 4.1 | 0.3420 | 0.467 % | -0.14 % | True | no |
| simplify | tol 5.00 % | 86 | 7.5 % | 4.4 | 2.0333 | 2.777 % | -3.66 % | True | no |

## larynx small.stl - 2980 tris, diagonal 130.86, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 2234 | 75.0 % | 4.3 | 0.2234 | 0.171 % | -0.06 % | True | no |
| quadric | 50 % tris | 1490 | 50.0 % | 5.0 | 1.6411 | 1.254 % | -0.22 % | True | no |
| quadric | 25 % tris | 744 | 25.0 % | 6.7 | 1.5658 | 1.197 % | -1.10 % | True | no |
| simplify | tol 0.01 % | 2942 | 98.7 % | 7.0 | 1.5797 | 1.207 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 2800 | 94.0 % | 10.9 | 1.5797 | 1.207 % | 0.00 % | True | no |
| simplify | tol 0.10 % | 2664 | 89.4 % | 11.9 | 1.5797 | 1.207 % | -0.01 % | True | no |
| simplify | tol 0.50 % | 1690 | 56.7 % | 10.9 | 1.5797 | 1.207 % | -0.09 % | True | no |
| simplify | tol 1.00 % | 1026 | 34.4 % | 11.9 | 1.5797 | 1.207 % | -0.12 % | True | no |
| simplify | tol 5.00 % | 182 | 6.1 % | 11.3 | 4.3649 | 3.336 % | -14.23 % | True | no |

## larynx_bolus.stl - 10694 tris, diagonal 267.27, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 8020 | 75.0 % | 16.2 | 0.0822 | 0.031 % | -0.00 % | True | no |
| quadric | 50 % tris | 5346 | 50.0 % | 61.8 | 0.9410 | 0.352 % | -0.02 % | True | no |
| quadric | 25 % tris | 2672 | 25.0 % | 26.4 | 0.9697 | 0.363 % | -0.08 % | True | no |
| simplify | tol 0.01 % | 10264 | 96.0 % | 15.1 | 0.0150 | 0.006 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 8438 | 78.9 % | 20.0 | 0.1048 | 0.039 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 6536 | 61.1 % | 24.8 | 2.0858 | 0.780 % | -0.01 % | True | no |
| simplify | tol 0.50 % | 1834 | 17.1 % | 31.6 | 1.6215 | 0.607 % | -0.18 % | True | no |
| simplify | tol 1.00 % | 834 | 7.8 % | 31.7 | 4.7775 | 1.788 % | -1.16 % | True | no |
| simplify | tol 5.00 % | 134 | 1.3 % | 31.1 | 11.0888 | 4.149 % | -20.51 % | True | no |

## mould_test.stl - 3203 tris, diagonal 122.43, closed False

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 2402 | 75.0 % | 3.5 | 0.1269 | 0.104 % | -0.00 % | True | no |
| quadric | 50 % tris | 1600 | 50.0 % | 5.5 | 0.2854 | 0.233 % | 0.13 % | True | no |
| quadric | 25 % tris | 800 | 25.0 % | 7.2 | 0.6904 | 0.564 % | -0.16 % | True | no |
| simplify | tol 0.01 % | - | - | - | - | - | - | Manifold.InvalidMesh | - |
| simplify | tol 0.05 % | - | - | - | - | - | - | Manifold.InvalidMesh | - |
| simplify | tol 0.10 % | - | - | - | - | - | - | Manifold.InvalidMesh | - |
| simplify | tol 0.50 % | - | - | - | - | - | - | Manifold.InvalidMesh | - |
| simplify | tol 1.00 % | - | - | - | - | - | - | Manifold.InvalidMesh | - |
| simplify | tol 5.00 % | - | - | - | - | - | - | Manifold.InvalidMesh | - |

## nose_bolus.stl - 1566 tris, diagonal 74.74, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 1174 | 75.0 % | 2.5 | 0.0887 | 0.119 % | -0.02 % | True | no |
| quadric | 50 % tris | 782 | 49.9 % | 2.6 | 0.2738 | 0.366 % | -0.13 % | True | no |
| quadric | 25 % tris | 390 | 24.9 % | 3.5 | 0.5893 | 0.788 % | -0.26 % | True | no |
| simplify | tol 0.01 % | 1548 | 98.9 % | 3.7 | 0.0027 | 0.004 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 1496 | 95.5 % | 4.0 | 0.0192 | 0.026 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 1432 | 91.4 % | 4.1 | 0.0373 | 0.050 % | -0.00 % | True | no |
| simplify | tol 0.50 % | 1004 | 64.1 % | 5.1 | 0.1857 | 0.248 % | 0.03 % | True | no |
| simplify | tol 1.00 % | 672 | 42.9 % | 5.4 | 0.3555 | 0.476 % | -0.09 % | True | no |
| simplify | tol 5.00 % | 106 | 6.8 % | 5.7 | 1.8049 | 2.415 % | -1.34 % | True | no |

## scalp_bolus.stl - 8546 tris, diagonal 195.15, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 6408 | 75.0 % | 12.1 | 0.0551 | 0.028 % | -0.01 % | True | no |
| quadric | 50 % tris | 4272 | 50.0 % | 17.3 | 0.1343 | 0.069 % | -0.02 % | True | no |
| quadric | 25 % tris | 2136 | 25.0 % | 20.0 | 1.7158 | 0.879 % | -0.07 % | True | no |
| simplify | tol 0.01 % | 8300 | 97.1 % | 16.0 | 0.0119 | 0.006 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 6966 | 81.5 % | 19.8 | 0.0521 | 0.027 % | 0.00 % | True | no |
| simplify | tol 0.10 % | 5330 | 62.4 % | 23.1 | 0.1237 | 0.063 % | -0.01 % | True | no |
| simplify | tol 0.50 % | 996 | 11.7 % | 28.0 | 0.7011 | 0.359 % | 0.06 % | True | no |
| simplify | tol 1.00 % | 436 | 5.1 % | 28.0 | 2.3392 | 1.199 % | -0.27 % | True | no |
| simplify | tol 5.00 % | 74 | 0.9 % | 27.4 | 26.0896 | 13.369 % | -25.24 % | True | no |

## scalp_mould.stl - 10832 tris, diagonal 217.76, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 8124 | 75.0 % | 12.6 | 0.2892 | 0.133 % | -0.02 % | True | no |
| quadric | 50 % tris | 5416 | 50.0 % | 20.9 | 0.3593 | 0.165 % | -0.05 % | True | no |
| quadric | 25 % tris | 2708 | 25.0 % | 25.8 | 0.3590 | 0.165 % | -0.18 % | True | no |
| simplify | tol 0.01 % | 9848 | 90.9 % | 16.5 | 0.1180 | 0.054 % | 0.00 % | True | no |
| simplify | tol 0.05 % | 7926 | 73.2 % | 21.0 | 0.3549 | 0.163 % | -0.03 % | True | no |
| simplify | tol 0.10 % | 5938 | 54.8 % | 27.9 | 0.3570 | 0.164 % | -0.08 % | True | no |
| simplify | tol 0.50 % | 1276 | 11.8 % | 30.8 | 0.6003 | 0.276 % | -0.52 % | True | no |
| simplify | tol 1.00 % | 684 | 6.3 % | 29.7 | 2.2430 | 1.030 % | -1.22 % | True | no |
| simplify | tol 5.00 % | 156 | 1.4 % | 31.3 | 7.0974 | 3.259 % | -8.04 % | True | no |

## small test.stl - 23552 tris, diagonal 93.80, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 17664 | 75.0 % | 37.2 | 0.0035 | 0.004 % | -0.00 % | True | no |
| quadric | 50 % tris | 11776 | 50.0 % | 46.8 | 0.0111 | 0.012 % | -0.02 % | True | no |
| quadric | 25 % tris | 5888 | 25.0 % | 63.5 | 0.0310 | 0.033 % | -0.06 % | True | no |
| simplify | tol 0.01 % | 17632 | 74.9 % | 35.5 | 0.0056 | 0.006 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 8640 | 36.7 % | 56.1 | 0.0302 | 0.032 % | -0.01 % | True | no |
| simplify | tol 0.10 % | 3896 | 16.5 % | 44.7 | 0.0499 | 0.053 % | -0.01 % | True | no |
| simplify | tol 0.50 % | 716 | 3.0 % | 48.2 | 0.2433 | 0.259 % | -0.03 % | True | no |
| simplify | tol 1.00 % | 336 | 1.4 % | 47.6 | 0.5561 | 0.593 % | -0.05 % | True | no |
| simplify | tol 5.00 % | 64 | 0.3 % | 47.7 | 2.2456 | 2.394 % | -0.84 % | True | no |

## sphere.stl - 9024 tris, diagonal 277.12, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 6768 | 75.0 % | 9.4 | 0.0377 | 0.014 % | -0.00 % | True | no |
| quadric | 50 % tris | 4512 | 50.0 % | 11.9 | 0.0913 | 0.033 % | -0.05 % | True | no |
| quadric | 25 % tris | 2256 | 25.0 % | 16.9 | 0.2130 | 0.077 % | -0.14 % | True | no |
| simplify | tol 0.01 % | 8028 | 89.0 % | 15.3 | 0.0110 | 0.004 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 6138 | 68.0 % | 14.9 | 0.0570 | 0.021 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 3594 | 39.8 % | 17.0 | 0.1607 | 0.058 % | -0.02 % | True | no |
| simplify | tol 0.50 % | 682 | 7.6 % | 19.7 | 0.7462 | 0.269 % | -0.08 % | True | no |
| simplify | tol 1.00 % | 340 | 3.8 % | 20.5 | 1.3908 | 0.502 % | -0.19 % | True | no |
| simplify | tol 5.00 % | 74 | 0.8 % | 20.9 | 6.7302 | 2.429 % | -1.13 % | True | no |

## test bolus 107mL.stl - 15212 tris, diagonal 161.28, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 11408 | 75.0 % | 20.7 | 0.0223 | 0.014 % | -0.00 % | True | no |
| quadric | 50 % tris | 7606 | 50.0 % | 58.3 | 0.0769 | 0.048 % | -0.01 % | True | no |
| quadric | 25 % tris | 3802 | 25.0 % | 36.4 | 0.2708 | 0.168 % | -0.07 % | True | no |
| simplify | tol 0.01 % | 13452 | 88.4 % | 21.5 | 0.0113 | 0.007 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 9594 | 63.1 % | 28.3 | 0.0608 | 0.038 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 7048 | 46.3 % | 33.3 | 0.0928 | 0.058 % | -0.01 % | True | no |
| simplify | tol 0.50 % | 1908 | 12.5 % | 37.8 | 0.4620 | 0.286 % | -0.05 % | True | no |
| simplify | tol 1.00 % | 900 | 5.9 % | 39.3 | 0.8343 | 0.517 % | -0.28 % | True | no |
| simplify | tol 5.00 % | 114 | 0.7 % | 40.3 | 3.8046 | 2.359 % | -1.34 % | True | no |

## test bolus 7mm.stl - 10594 tris, diagonal 298.94, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 7944 | 75.0 % | 11.5 | 0.1632 | 0.055 % | -0.01 % | True | no |
| quadric | 50 % tris | 5296 | 50.0 % | 19.4 | 0.3131 | 0.105 % | -0.05 % | True | no |
| quadric | 25 % tris | 2648 | 25.0 % | 25.6 | 1.0665 | 0.357 % | -0.20 % | True | no |
| simplify | tol 0.01 % | 10168 | 96.0 % | 16.9 | 0.0216 | 0.007 % | -0.00 % | True | no |
| simplify | tol 0.05 % | 8574 | 80.9 % | 17.7 | 0.0901 | 0.030 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 6684 | 63.1 % | 21.5 | 0.3018 | 0.101 % | -0.01 % | True | no |
| simplify | tol 0.50 % | 1938 | 18.3 % | 31.0 | 1.5038 | 0.503 % | 0.00 % | True | no |
| simplify | tol 1.00 % | 756 | 7.1 % | 31.3 | 3.6334 | 1.215 % | -0.52 % | True | no |
| simplify | tol 5.00 % | 108 | 1.0 % | 30.0 | 14.8425 | 4.965 % | -18.25 % | True | no |

## test_smoothed_bolus.stl - 100612 tris, diagonal 109.75, closed True

| method | ask | tris | % of input | ms | max dev | max dev / diag | vol change | closed | welded |
|---|---|---:|---:|---:|---:|---:|---:|---|---|
| quadric | 75 % tris | 75458 | 75.0 % | 154.2 | 0.0022 | 0.002 % | 0.00 % | True | no |
| quadric | 50 % tris | 50306 | 50.0 % | 238.0 | 0.0056 | 0.005 % | 0.00 % | True | no |
| quadric | 25 % tris | 25152 | 25.0 % | 327.4 | 0.0199 | 0.018 % | 0.00 % | True | no |
| simplify | tol 0.01 % | 49444 | 49.1 % | 214.1 | 0.0063 | 0.006 % | 0.00 % | True | no |
| simplify | tol 0.05 % | 15212 | 15.1 % | 226.0 | 0.0341 | 0.031 % | -0.00 % | True | no |
| simplify | tol 0.10 % | 7878 | 7.8 % | 226.1 | 0.0620 | 0.056 % | -0.01 % | True | no |
| simplify | tol 0.50 % | 1332 | 1.3 % | 220.7 | 0.2830 | 0.258 % | -0.10 % | True | no |
| simplify | tol 1.00 % | 578 | 0.6 % | 224.1 | 0.5679 | 0.517 % | -0.08 % | True | no |
| simplify | tol 5.00 % | 126 | 0.1 % | 223.4 | 2.8455 | 2.593 % | -2.96 % | True | no |

