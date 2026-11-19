# Rules for agents

## Benchmarks

- Every benchmarking run, meaning any run whose times are reported, compared or written into the docs, runs on Google Cloud Build, through the pool in `.github/actions/pool`, as `.github/workflows/bench.yml` does. Times are taken with BenchmarkDotNet (`tests/Mida.Tests/Timed.cs`).
- Locally, run benchmarks and the `RealWorld` tests only to check correctness: hits and misses, which tests ran, and the same files as `dotnet build`. The times a local run prints are not results, so do not report them, compare them or write them down. A laptop shares its processors with everything else on it, and one run on a loaded Mac put the same plain build anywhere from 10.9 s to 25.4 s.
