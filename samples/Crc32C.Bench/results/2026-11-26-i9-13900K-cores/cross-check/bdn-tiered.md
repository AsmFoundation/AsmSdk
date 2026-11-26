```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9974/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 11.0.100
  [Host] : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                           | Size    | Mean           | Error       | StdDev      | GB/s  | Median         | Ratio | RatioSD |
|--------------------------------- |-------- |---------------:|------------:|------------:|------:|---------------:|------:|--------:|
| 'C# BitOperations.Crc32C'        | 64      |       4.387 ns |   0.0533 ns |   0.0989 ns | 14.59 |       4.332 ns |  1.00 |    0.03 |
| 'asm crc32c_append (dispatcher)' | 64      |      24.199 ns |   0.0179 ns |   0.0167 ns |  2.64 |      24.203 ns |  5.52 |    0.12 |
|                                  |         |                |             |             |       |                |       |         |
| 'C# BitOperations.Crc32C'        | 256     |      28.075 ns |   0.0399 ns |   0.0354 ns |  9.12 |      28.078 ns |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 256     |      39.488 ns |   0.3159 ns |   0.2638 ns |  6.48 |      39.308 ns |  1.41 |    0.01 |
|                                  |         |                |             |             |       |                |       |         |
| 'C# BitOperations.Crc32C'        | 1024    |     147.262 ns |   0.1591 ns |   0.1489 ns |  6.95 |     147.235 ns |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 1024    |      54.733 ns |   0.0362 ns |   0.0302 ns | 18.71 |      54.733 ns |  0.37 |    0.00 |
|                                  |         |                |             |             |       |                |       |         |
| 'C# BitOperations.Crc32C'        | 65536   |  11,400.431 ns |   5.2625 ns |   4.3944 ns |  5.75 |  11,399.065 ns |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 65536   |   3,264.556 ns |   1.8132 ns |   1.6960 ns | 20.08 |   3,264.028 ns |  0.29 |    0.00 |
|                                  |         |                |             |             |       |                |       |         |
| 'C# BitOperations.Crc32C'        | 1048576 | 182,645.311 ns | 126.4225 ns | 105.5685 ns |  5.74 | 182,660.913 ns |  1.00 |    0.00 |
| 'asm crc32c_append (dispatcher)' | 1048576 |  52,734.361 ns | 272.2563 ns | 227.3464 ns | 19.88 |  52,771.454 ns |  0.29 |    0.00 |
