using Xunit.Sdk;
using Xunit.v3;

// xUnit.net 4 runs every test case of this assembly in parallel (Mode = All), including the cases of one class or
// [Collection]: [Collection] alone does NOT serialize anything here. The tests create their own state (their own
// PostgreSQL database, temporary directories, service instances) and must not rely on running one after the other.
// Tests that share process-wide state or block threads opt out with [Fact/Theory(DisableParallelization = true)], or
// with [CollectionDefinition(..., DisableParallelization = true)] for a group; those run one after the other in a
// non-parallel phase after the parallel tests. The number of test threads stays at the default (one per CPU
// thread). Override per run without recompiling: -parallelMode / -maxThreads (see eng/Test-WithPostgres.ps1).
[assembly: Parallelization(Mode = ParallelMode.All)]
