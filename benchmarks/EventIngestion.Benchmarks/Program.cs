using BenchmarkDotNet.Running;
using EventIngestion.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(IngestBenchmarks).Assembly).Run(args);
