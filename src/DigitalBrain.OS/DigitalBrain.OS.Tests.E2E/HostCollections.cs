namespace DigitalBrain.OS.Tests.E2E;

// A configuration starts only when its collection is selected; each host is its own AppHost with its own model
// endpoint, dashboard and shell container, so the collections run in parallel. Filtered app runs start just the reference host.
[CollectionDefinition("Reference host")]
public sealed class ReferenceHostCollection : ICollectionFixture<ReferenceBrainFixture>;

[CollectionDefinition("Product host")]
public sealed class ProductHostCollection : ICollectionFixture<ProductHostFixture>;

[CollectionDefinition("Router host")]
public sealed class RouterHostCollection : ICollectionFixture<RouterHostFixture>;

// Self-improvement builds a candidate in Docker and rolls it beside the cluster through the machine's one Aspire CLI
// cluster host: machine-wide state no other host may share, so its fact runs alone after the parallel collections.
[CollectionDefinition("Candidate host", DisableParallelization = true)]
public sealed class CandidateHostCollection;
