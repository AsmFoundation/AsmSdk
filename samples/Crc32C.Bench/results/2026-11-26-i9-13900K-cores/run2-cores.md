# Crc32C.Bench --cores: the C# loop and the assembly on one core and on several

- started: 2026-11-26 05:37:24 UTC
- CPU: 13th Gen Intel(R) Core(TM) i9-13900K, 32 logical CPUs
- OS: Microsoft Windows 10.0.26200; runtime: .NET 10.0.1
- the assembly: crc32c_append through Crc32C.Net, dispatcher path HardwareThreeWay
- the C# loop: BitOperations.Crc32C over 8-byte words
- threads pinned to logical CPUs 2,4,6,8 (thread i on the i-th)
- power throttling: off for this process; process priority class: High
- 12 paired rounds; each cell runs 0.1 s of warm-up, then a 0.5 s window
- core counts 1, 4; sizes 64 B, 256 B, 1 KiB, 64 KiB, 1 MiB

round 1 of 12 done in 12.2 s
round 2 of 12 done in 12.2 s
round 3 of 12 done in 12.3 s
round 4 of 12 done in 12.2 s
round 5 of 12 done in 12.2 s
round 6 of 12 done in 12.2 s
round 7 of 12 done in 12.2 s
round 8 of 12 done in 12.2 s
round 9 of 12 done in 12.2 s
round 10 of 12 done in 12.2 s
round 11 of 12 done in 12.2 s
round 12 of 12 done in 12.2 s

## Medians of 12 paired rounds

GB/s and calls/s are all cores together; "per call" is the time one call takes on its core while the others run.

| Size | Cores | C# GB/s | asm GB/s | C# calls/s | asm calls/s | C# per call | asm per call | asm speed / C# | asm faster in |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 64 B | 1 | 12.1 | 7.34 | 189M | 115M | 5.28 ns | 8.72 ns | 0.61x | 0 of 12 |
| 64 B | 4 | 48.5 | 29.5 | 757M | 461M | 5.28 ns | 8.68 ns | 0.61x | 0 of 12 |
| 256 B | 1 | 9.33 | 19.1 | 36.5M | 74.5M | 27.4 ns | 13.4 ns | 2.04x | 12 of 12 |
| 256 B | 4 | 37.4 | 76.4 | 146M | 298M | 27.4 ns | 13.4 ns | 2.05x | 12 of 12 |
| 1 KiB | 1 | 6.78 | 19.5 | 6.62M | 19.1M | 151 ns | 52.4 ns | 2.88x | 12 of 12 |
| 1 KiB | 4 | 27.2 | 78.2 | 26.5M | 76.4M | 151 ns | 52.4 ns | 2.88x | 12 of 12 |
| 64 KiB | 1 | 5.76 | 20.1 | 87.9k | 307k | 11.4 us | 3.26 us | 3.49x | 12 of 12 |
| 64 KiB | 4 | 23.1 | 80.4 | 352k | 1.23M | 11.4 us | 3.26 us | 3.49x | 12 of 12 |
| 1 MiB | 1 | 5.74 | 20.0 | 5.48k | 19.1k | 183 us | 52.3 us | 3.49x | 12 of 12 |
| 1 MiB | 4 | 23.0 | 80.0 | 21.9k | 76.3k | 183 us | 52.4 us | 3.48x | 12 of 12 |

## Spread: the lowest and the highest round, GB/s

| Size | Cores | C# | asm | asm speed / C#, per round |
|---|---:|---:|---:|---:|
| 64 B | 1 | 11.7 to 12.1 | 7.19 to 7.37 | 0.60x to 0.61x |
| 64 B | 4 | 47.9 to 48.5 | 28.8 to 29.6 | 0.59x to 0.62x |
| 256 B | 1 | 9.15 to 9.34 | 18.6 to 19.1 | 2.04x to 2.05x |
| 256 B | 4 | 36.9 to 37.4 | 75.4 to 76.6 | 2.02x to 2.06x |
| 1 KiB | 1 | 6.73 to 6.79 | 19.3 to 19.6 | 2.86x to 2.89x |
| 1 KiB | 4 | 26.9 to 27.2 | 78.0 to 78.3 | 2.87x to 2.90x |
| 64 KiB | 1 | 5.72 to 5.76 | 19.9 to 20.1 | 3.48x to 3.49x |
| 64 KiB | 4 | 22.8 to 23.1 | 79.6 to 80.5 | 3.48x to 3.50x |
| 1 MiB | 1 | 5.70 to 5.75 | 19.7 to 20.1 | 3.45x to 3.50x |
| 1 MiB | 4 | 22.9 to 23.0 | 78.9 to 80.5 | 3.44x to 3.50x |

## Scaling: N cores over 1 core, median of the per-round ratios

| Size | C# x4 | asm x4 |
|---|---:|---:|
| 64 B | 4.00x | 4.02x |
| 256 B | 4.00x | 4.00x |
| 1 KiB | 4.00x | 4.00x |
| 64 KiB | 4.00x | 4.00x |
| 1 MiB | 4.00x | 3.99x |

raw counts: C:\Users\ELI\AppData\Local\Temp\claude\87feb1de4284698d\5893c2da-d4f5-408e-8d3f-e795f8b2b785\scratchpad\runs\run2-cores.json
