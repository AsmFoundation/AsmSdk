```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 16 physical cores
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                         | Mean     | Error     | StdDev    | GB/s | Ratio |
|------------------------------- |---------:|----------:|----------:|-----:|------:|
| &#39;call, with the GC transition&#39; | 8.206 ns | 0.0747 ns | 0.0624 ns |    ? |  1.00 |
| &#39;call, SuppressGCTransition&#39;   | 3.295 ns | 0.0348 ns | 0.0326 ns |    ? |  0.40 |
