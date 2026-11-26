# Crc32C.Bench --cores: the C# loop and the assembly on one core and on several

- started: 2026-11-26 05:39:56 UTC
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
round 3 of 12 done in 12.2 s
round 4 of 12 done in 12.2 s
round 5 of 12 done in 12.2 s
round 6 of 12 done in 12.2 s
round 7 of 12 done in 12.2 s
round 8 of 12 done in 12.2 s
round 9 of 12 done in 12.3 s
round 10 of 12 done in 12.2 s
round 11 of 12 done in 12.2 s
round 12 of 12 done in 12.2 s

## Medians of 12 paired rounds

GB/s and calls/s are all cores together; "per call" is the time one call takes on its core while the others run.

| Size | Cores | C# GB/s | asm GB/s | C# calls/s | asm calls/s | C# per call | asm per call | asm speed / C# | asm faster in |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 64 B | 1 | 9.83 | 7.44 | 154M | 116M | 6.51 ns | 8.60 ns | 0.76x | 0 of 12 |
| 64 B | 4 | 37.9 | 29.9 | 593M | 467M | 6.75 ns | 8.57 ns | 0.79x | 0 of 12 |
| 256 B | 1 | 9.27 | 19.2 | 36.2M | 75.1M | 27.6 ns | 13.3 ns | 2.07x | 12 of 12 |
| 256 B | 4 | 36.5 | 76.9 | 143M | 300M | 28.1 ns | 13.3 ns | 2.10x | 12 of 12 |
| 1 KiB | 1 | 6.75 | 19.6 | 6.59M | 19.1M | 152 ns | 52.4 ns | 2.90x | 12 of 12 |
| 1 KiB | 4 | 27.0 | 78.2 | 26.4M | 76.4M | 152 ns | 52.4 ns | 2.90x | 12 of 12 |
| 64 KiB | 1 | 5.76 | 20.1 | 87.9k | 306k | 11.4 us | 3.26 us | 3.49x | 12 of 12 |
| 64 KiB | 4 | 23.0 | 80.3 | 352k | 1.23M | 11.4 us | 3.26 us | 3.49x | 12 of 12 |
| 1 MiB | 1 | 5.74 | 20.1 | 5.47k | 19.1k | 183 us | 52.3 us | 3.49x | 12 of 12 |
| 1 MiB | 4 | 22.9 | 79.4 | 21.9k | 75.7k | 183 us | 52.8 us | 3.46x | 12 of 12 |

## Spread: the lowest and the highest round, GB/s

| Size | Cores | C# | asm | asm speed / C#, per round |
|---|---:|---:|---:|---:|
| 64 B | 1 | 9.75 to 9.94 | 7.38 to 7.49 | 0.74x to 0.76x |
| 64 B | 4 | 36.5 to 38.8 | 29.6 to 30.0 | 0.77x to 0.82x |
| 256 B | 1 | 9.25 to 9.32 | 19.1 to 19.3 | 2.06x to 2.08x |
| 256 B | 4 | 36.1 to 36.9 | 76.8 to 77.2 | 2.09x to 2.13x |
| 1 KiB | 1 | 6.69 to 6.77 | 19.3 to 19.6 | 2.88x to 2.90x |
| 1 KiB | 4 | 26.7 to 27.0 | 76.5 to 78.3 | 2.86x to 2.91x |
| 64 KiB | 1 | 5.73 to 5.77 | 19.9 to 20.1 | 3.47x to 3.49x |
| 64 KiB | 4 | 22.9 to 23.1 | 79.5 to 80.4 | 3.46x to 3.50x |
| 1 MiB | 1 | 5.69 to 5.75 | 19.3 to 20.1 | 3.40x to 3.50x |
| 1 MiB | 4 | 22.8 to 23.0 | 77.9 to 80.4 | 3.42x to 3.51x |

## Scaling: N cores over 1 core, median of the per-round ratios

| Size | C# x4 | asm x4 |
|---|---:|---:|
| 64 B | 3.82x | 4.02x |
| 256 B | 3.94x | 4.00x |
| 1 KiB | 4.00x | 4.00x |
| 64 KiB | 4.00x | 4.00x |
| 1 MiB | 4.00x | 3.99x |

raw counts: C:\Users\ELI\AppData\Local\Temp\claude\87feb1de4284698d\5893c2da-d4f5-408e-8d3f-e795f8b2b785\scratchpad\runs\run3-cores.json
