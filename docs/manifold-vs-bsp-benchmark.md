# Manifold vs managed BSP — BenchmarkDotNet results, 2026-09-10

Both backends in one job, so only the kernel differs. Speedups: 40x (1.2k tris) to 142x
(100.6k tris). See docs/proposals/perf-and-native-evaluation.md for the interpretation.

The Allocated column is NOT like-for-like: the diagnoser counts managed bytes only, so the
manifold rows show just the marshalling buffers, not what the native library allocates.

```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6466/22H2/2022Update)
AMD Ryzen 5 5600, 1 CPU, 12 logical and 6 physical cores
.NET SDK 9.0.316
  [Host]   : .NET 8.0.29 (8.0.2926.32403), X64 RyuJIT AVX2
  ShortRun : .NET 8.0.29 (8.0.2926.32403), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method    | File                 | Backend  | Mean          | Error         | StdDev      | Gen0         | Gen1        | Gen2       | Allocated      |
|---------- |--------------------- |--------- |--------------:|--------------:|------------:|-------------:|------------:|-----------:|---------------:|
| **Union**     | **chin_bolus.stl**       | **bsp**      |    **493.763 ms** |   **114.7028 ms** |   **6.2872 ms** |   **70000.0000** |   **1000.0000** |          **-** |  **1150262.24 KB** |
| Subtract  | chin_bolus.stl       | bsp      |    499.154 ms |   937.8902 ms |  51.4089 ms |   70000.0000 |   1000.0000 |          - |  1147302.47 KB |
| Intersect | chin_bolus.stl       | bsp      |    410.381 ms |    34.1445 ms |   1.8716 ms |   69000.0000 |   1000.0000 |          - |  1132927.89 KB |
| **Union**     | **chin_bolus.stl**       | **manifold** |      **7.373 ms** |     **1.0404 ms** |   **0.0570 ms** |      **15.6250** |           **-** |          **-** |      **296.78 KB** |
| Subtract  | chin_bolus.stl       | manifold |      7.035 ms |     0.6361 ms |   0.0349 ms |       7.8125 |           - |          - |      243.31 KB |
| Intersect | chin_bolus.stl       | manifold |      8.001 ms |     1.6539 ms |   0.0907 ms |      15.6250 |           - |          - |      329.14 KB |
| **Union**     | **eye_bolus.stl**        | **bsp**      |    **211.754 ms** |    **64.1806 ms** |   **3.5180 ms** |   **25000.0000** |   **1666.6667** |  **1000.0000** |   **400234.39 KB** |
| Subtract  | eye_bolus.stl        | bsp      |    187.131 ms |    24.7148 ms |   1.3547 ms |   24000.0000 |   1000.0000 |          - |   397650.79 KB |
| Intersect | eye_bolus.stl        | bsp      |    127.354 ms |    17.6060 ms |   0.9650 ms |   22000.0000 |   1000.0000 |          - |   367909.01 KB |
| **Union**     | **eye_bolus.stl**        | **manifold** |      **5.074 ms** |     **0.6444 ms** |   **0.0353 ms** |       **7.8125** |           **-** |          **-** |      **200.15 KB** |
| Subtract  | eye_bolus.stl        | manifold |      4.735 ms |     0.4312 ms |   0.0236 ms |       7.8125 |           - |          - |      159.74 KB |
| Intersect | eye_bolus.stl        | manifold |      4.538 ms |     0.5140 ms |   0.0282 ms |       7.8125 |           - |          - |      136.52 KB |
| **Union**     | **small test.stl**       | **bsp**      |  **6,139.906 ms** |   **659.6287 ms** |  **36.1565 ms** |  **296000.0000** | **122000.0000** | **11000.0000** |  **6685820.52 KB** |
| Subtract  | small test.stl       | bsp      |  5,653.240 ms |   310.1224 ms |  16.9989 ms |  263000.0000 |  79000.0000 | 10000.0000 |  6168443.13 KB |
| Intersect | small test.stl       | bsp      |  5,241.032 ms |   315.7067 ms |  17.3050 ms |  252000.0000 |  75000.0000 | 10000.0000 |  5960292.07 KB |
| **Union**     | **small test.stl**       | **manifold** |     **42.651 ms** |    **10.0931 ms** |   **0.5532 ms** |     **636.3636** |    **636.3636** |   **636.3636** |     **2220.36 KB** |
| Subtract  | small test.stl       | manifold |     42.476 ms |     3.8654 ms |   0.2119 ms |     500.0000 |    500.0000 |   500.0000 |     2233.16 KB |
| Intersect | small test.stl       | manifold |     31.981 ms |     1.4576 ms |   0.0799 ms |     250.0000 |    250.0000 |   250.0000 |     1108.46 KB |
| **Union**     | **test_(...)s.stl [23]** | **bsp**      | **18,184.111 ms** | **4,881.0656 ms** | **267.5477 ms** | **2201000.0000** |  **25000.0000** |  **6000.0000** | **35985977.21 KB** |
| Subtract  | test_(...)s.stl [23] | bsp      | 18,864.637 ms | 2,369.5492 ms | 129.8830 ms | 2213000.0000 |  38000.0000 |  5000.0000 | 36188108.47 KB |
| Intersect | test_(...)s.stl [23] | bsp      | 19,339.798 ms |   283.2862 ms |  15.5279 ms | 2163000.0000 |  37000.0000 |  5000.0000 | 35369789.45 KB |
| **Union**     | **test_(...)s.stl [23]** | **manifold** |    **133.830 ms** |    **12.4549 ms** |   **0.6827 ms** |     **750.0000** |    **750.0000** |   **750.0000** |     **6481.14 KB** |
| Subtract  | test_(...)s.stl [23] | manifold |    132.884 ms |    34.6934 ms |   1.9017 ms |     750.0000 |    750.0000 |   750.0000 |     6437.61 KB |
| Intersect | test_(...)s.stl [23] | manifold |    140.348 ms |    15.5196 ms |   0.8507 ms |     750.0000 |    750.0000 |   750.0000 |     7058.33 KB |
