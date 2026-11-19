```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat) (container)
13th Gen Intel Core i9-13900K 3.00GHz, 1 CPU, 32 logical and 16 physical cores
  [Host] : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                         | Mean      | Error     | StdDev    | GB/s | Median    | Ratio | RatioSD |
|------------------------------- |----------:|----------:|----------:|-----:|----------:|------:|--------:|
| &#39;call, with the GC transition&#39; | 10.060 ns | 0.0734 ns | 0.0651 ns |    ? | 10.082 ns |  1.00 |    0.01 |
| &#39;call, SuppressGCTransition&#39;   |  4.152 ns | 0.1278 ns | 0.3206 ns |    ? |  4.041 ns |  0.41 |    0.03 |
