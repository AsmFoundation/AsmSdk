```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9974/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 11.0.100
  [Host] : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                           | Size    | Mean         | Error      | StdDev     | GB/s  |
|--------------------------------- |-------- |-------------:|-----------:|-----------:|------:|
| 'asm crc32c_append (dispatcher)' | 64      |     24.28 ns |   0.062 ns |   0.052 ns |  2.64 |
| 'asm crc32c_append (dispatcher)' | 256     |     39.50 ns |   0.021 ns |   0.019 ns |  6.48 |
| 'asm crc32c_append (dispatcher)' | 1024    |     90.88 ns |   0.043 ns |   0.040 ns | 11.27 |
| 'asm crc32c_append (dispatcher)' | 65536   |  3,295.37 ns |   1.620 ns |   1.265 ns | 19.89 |
| 'asm crc32c_append (dispatcher)' | 1048576 | 52,271.00 ns | 133.720 ns | 111.662 ns | 20.06 |
