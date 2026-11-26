```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9974/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 11.0.100
  [Host] : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                         | Mean     | Error     | StdDev    | Ratio |
|------------------------------- |---------:|----------:|----------:|------:|
| &#39;call, with the GC transition&#39; | 7.609 ns | 0.0490 ns | 0.0435 ns |  1.00 |
| &#39;call, SuppressGCTransition&#39;   | 4.704 ns | 0.0169 ns | 0.0225 ns |  0.62 |
| &#39;call, generated binding&#39;      | 7.383 ns | 0.0256 ns | 0.0240 ns |  0.97 |
