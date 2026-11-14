using Xunit.Sdk;
using Xunit.v3;

// xUnit.net 4 runs every test case of this assembly in parallel (Mode = All): the tests create their own state
// (their own PostgreSQL database, temporary directories, service instances) and do not rely on running one after
// the other. Classes that share process-wide state opt out with [Collection(...)] (tests of one collection run
// sequentially) and timing-sensitive tests would use [Fact(DisableParallelism = true)]. The number of test threads
// stays at the default (one per CPU thread): more threads only make the timing-sensitive tests flakier.
// Override per run without recompiling: -parallelMode / -maxThreads (see eng/Test-WithPostgres.ps1 -Parallel / -MaxThreads).
[assembly: Parallelization(Mode = ParallelMode.All)]
