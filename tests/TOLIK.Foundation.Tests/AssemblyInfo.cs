using Xunit;

// The integration tests (CoreHostFactory) spin real hosts against a shared PostgreSQL
// server; parallel execution under load caused flaky failures (53100 shared-memory
// exhaustion at the Docker default /dev/shm; TOTP 30s window slips under parallel
// delay). Sequential execution keeps every check — it only removes the flakiness.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
