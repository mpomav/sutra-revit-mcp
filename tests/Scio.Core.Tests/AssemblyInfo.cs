using Xunit;

// Several tests isolate state via process-wide environment variables (e.g. SCIO_DATA_ROOT)
// and static SessionRegistry overrides; run test collections sequentially for determinism.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
