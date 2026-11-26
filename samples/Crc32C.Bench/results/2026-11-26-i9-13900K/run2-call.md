```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9974/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 11.0.100
  [Host] : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                         | Mean     | Error     | StdDev    | Median   | Ratio | RatioSD |
|------------------------------- |---------:|----------:|----------:|---------:|------:|--------:|
| &#39;call, with the GC transition&#39; | 7.060 ns | 0.0724 ns | 0.0677 ns | 7.046 ns |  1.00 |    0.01 |
| &#39;call, SuppressGCTransition&#39;   | 4.877 ns | 0.1493 ns | 0.2535 ns | 4.719 ns |  0.69 |    0.04 |
| &#39;call, generated binding&#39;      | 7.420 ns | 0.0204 ns | 0.0181 ns | 7.424 ns |  1.05 |    0.01 |
