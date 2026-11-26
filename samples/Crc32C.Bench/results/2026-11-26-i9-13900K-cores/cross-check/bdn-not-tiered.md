```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9974/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 11.0.100
  [Host] : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                           | Size    | Mean           | Error       | StdDev      | GB/s  | Ratio | RatioSD |
|--------------------------------- |-------- |---------------:|------------:|------------:|------:|------:|--------:|
| 'C# BitOperations.Crc32C'        | 64      |       5.345 ns |   0.0625 ns |   0.0584 ns | 11.97 |  1.00 |    0.02 |
| 'asm crc32c_append (dispatcher)' | 64      |       9.915 ns |   0.0347 ns |   0.0324 ns |  6.45 |  1.86 |    0.02 |
|                                  |         |                |             |             |       |       |         |
| 'C# BitOperations.Crc32C'        | 256     |      28.378 ns |   0.0391 ns |   0.0327 ns |  9.02 |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 256     |      15.615 ns |   0.0123 ns |   0.0115 ns | 16.39 |  0.55 |    0.00 |
|                                  |         |                |             |             |       |       |         |
| 'C# BitOperations.Crc32C'        | 1024    |     150.420 ns |   0.2953 ns |   0.2762 ns |  6.81 |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 1024    |      54.889 ns |   0.0627 ns |   0.0556 ns | 18.66 |  0.36 |    0.00 |
|                                  |         |                |             |             |       |       |         |
| 'C# BitOperations.Crc32C'        | 65536   |  11,401.816 ns |  22.0715 ns |  19.5658 ns |  5.75 |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 65536   |   3,266.018 ns |   3.9301 ns |   3.2818 ns | 20.07 |  0.29 |    0.00 |
|                                  |         |                |             |             |       |       |         |
| 'C# BitOperations.Crc32C'        | 1048576 | 182,715.450 ns | 183.3900 ns | 143.1789 ns |  5.74 |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 1048576 |  52,755.200 ns | 346.0489 ns | 323.6943 ns | 19.88 |  0.29 |    0.00 |
