using BenchmarkDotNet.Running;

// dotnet run -c Release --project VideoQuality.Benchmarks -- --filter '*'
// Decoder benchmarks need real media: VQ_BENCH_REF / VQ_BENCH_DIST (see docs/development.md).
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
