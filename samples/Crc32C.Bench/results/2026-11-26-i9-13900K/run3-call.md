```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9974/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 11.0.100
  [Host] : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                         | Mean     | Error     | StdDev    | Median   | Ratio | RatioSD |
|------------------------------- |---------:|----------:|----------:|---------:|------:|--------:|
| &#39;call, with the GC transition&#39; | 7.355 ns | 0.1990 ns | 0.3689 ns | 7.161 ns |  1.00 |    0.07 |
| &#39;call, SuppressGCTransition&#39;   | 5.623 ns | 0.0281 ns | 0.0263 ns | 5.616 ns |  0.77 |    0.04 |
| &#39;call, generated binding&#39;      | 6.797 ns | 0.0240 ns | 0.0200 ns | 6.796 ns |  0.93 |    0.05 |
