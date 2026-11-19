```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 16 physical cores
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                         | Mean     | Error     | StdDev    | GB/s | Ratio | RatioSD |
|------------------------------- |---------:|----------:|----------:|-----:|------:|--------:|
| &#39;call, with the GC transition&#39; | 9.596 ns | 0.1398 ns | 0.1308 ns |    ? |  1.00 |    0.02 |
| &#39;call, SuppressGCTransition&#39;   | 3.383 ns | 0.0354 ns | 0.0296 ns |    ? |  0.35 |    0.01 |
