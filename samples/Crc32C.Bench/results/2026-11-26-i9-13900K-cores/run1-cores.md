# Crc32C.Bench --cores: the C# loop and the assembly on one core and on several

- started: 2026-11-26 05:18:28 UTC
- CPU: 13th Gen Intel(R) Core(TM) i9-13900K, 32 logical CPUs
- OS: Microsoft Windows 10.0.26200; runtime: .NET 10.0.1
- the assembly: crc32c_append through Crc32C.Net, dispatcher path HardwareThreeWay
- the C# loop: BitOperations.Crc32C over 8-byte words
- threads pinned to logical CPUs 2,4,6,8 (thread i on the i-th)
- power throttling: off for this process; process priority class: High
- 12 paired rounds; each cell runs 0.1 s of warm-up, then a 0.5 s window
- core counts 1, 4; sizes 64 B, 256 B, 1 KiB, 64 KiB, 1 MiB

round 1 of 12 done in 12.2 s
round 2 of 12 done in 12.3 s
round 3 of 12 done in 12.2 s
round 4 of 12 done in 12.2 s
round 5 of 12 done in 12.2 s
round 6 of 12 done in 12.1 s
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
| 64 B | 1 | 12.0 | 7.29 | 187M | 114M | 5.34 ns | 8.78 ns | 0.61x | 0 of 12 |
| 64 B | 4 | 48.0 | 29.4 | 749M | 459M | 5.34 ns | 8.72 ns | 0.61x | 0 of 12 |
| 256 B | 1 | 9.32 | 19.0 | 36.4M | 74.1M | 27.5 ns | 13.5 ns | 2.04x | 12 of 12 |
| 256 B | 4 | 37.3 | 76.1 | 146M | 297M | 27.5 ns | 13.5 ns | 2.04x | 12 of 12 |
| 1 KiB | 1 | 6.80 | 19.6 | 6.64M | 19.2M | 151 ns | 52.2 ns | 2.88x | 12 of 12 |
| 1 KiB | 4 | 27.2 | 78.5 | 26.6M | 76.7M | 150 ns | 52.2 ns | 2.88x | 12 of 12 |
| 64 KiB | 1 | 5.76 | 20.1 | 87.9k | 307k | 11.4 us | 3.26 us | 3.49x | 12 of 12 |
| 64 KiB | 4 | 23.1 | 80.4 | 352k | 1.23M | 11.4 us | 3.26 us | 3.49x | 12 of 12 |
| 1 MiB | 1 | 5.74 | 20.0 | 5.47k | 19.1k | 183 us | 52.4 us | 3.49x | 12 of 12 |
| 1 MiB | 4 | 22.9 | 80.0 | 21.9k | 76.3k | 183 us | 52.4 us | 3.48x | 12 of 12 |

## Spread: the lowest and the highest round, GB/s

| Size | Cores | C# | asm | asm speed / C#, per round |
|---|---:|---:|---:|---:|
| 64 B | 1 | 11.8 to 12.0 | 7.12 to 7.35 | 0.60x to 0.61x |
| 64 B | 4 | 47.8 to 48.0 | 29.3 to 29.5 | 0.61x to 0.62x |
| 256 B | 1 | 9.30 to 9.32 | 18.9 to 19.0 | 2.03x to 2.04x |
| 256 B | 4 | 37.1 to 37.3 | 75.3 to 76.2 | 2.02x to 2.05x |
| 1 KiB | 1 | 6.79 to 6.81 | 19.0 to 19.6 | 2.80x to 2.89x |
| 1 KiB | 4 | 27.2 to 27.2 | 78.4 to 78.6 | 2.88x to 2.89x |
| 64 KiB | 1 | 5.73 to 5.77 | 19.9 to 20.1 | 3.48x to 3.50x |
| 64 KiB | 4 | 23.0 to 23.1 | 80.2 to 80.5 | 3.48x to 3.49x |
| 1 MiB | 1 | 5.71 to 5.75 | 19.8 to 20.1 | 3.45x to 3.50x |
| 1 MiB | 4 | 22.8 to 23.0 | 79.1 to 80.2 | 3.46x to 3.49x |

## Scaling: N cores over 1 core, median of the per-round ratios

| Size | C# x4 | asm x4 |
|---|---:|---:|
| 64 B | 4.00x | 4.03x |
| 256 B | 4.00x | 4.01x |
| 1 KiB | 4.00x | 4.00x |
| 64 KiB | 4.00x | 4.00x |
| 1 MiB | 4.00x | 3.99x |

raw counts: C:\Users\ELI\AppData\Local\Temp\claude\87feb1de4284698d\5893c2da-d4f5-408e-8d3f-e795f8b2b785\scratchpad\runs\run1-cores.json
