namespace DigitalBrain.OS.Tests.E2E;

// A configuration starts only when its collection is selected, and is disposed
// before the next serial collection. Filtered app runs start just the reference host.
[CollectionDefinition("Reference host")]
public sealed class ReferenceHostCollection : ICollectionFixture<ReferenceBrainFixture>;

[CollectionDefinition("Product host")]
public sealed class ProductHostCollection : ICollectionFixture<ProductHostFixture>;

[CollectionDefinition("Router host")]
public sealed class RouterHostCollection : ICollectionFixture<RouterHostFixture>;
