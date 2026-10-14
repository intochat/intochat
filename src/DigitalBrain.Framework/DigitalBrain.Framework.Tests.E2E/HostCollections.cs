namespace DigitalBrain.Framework.Tests.E2E;

[CollectionDefinition(nameof(ReferenceBrainFixture))]
public sealed class ReferenceHostCollection : ICollectionFixture<ReferenceBrainFixture>;

[CollectionDefinition(nameof(ProductionBrainFixture))]
public sealed class ProductionHostCollection : ICollectionFixture<ProductionBrainFixture>;

[CollectionDefinition(nameof(ProductionCaptureOffFixture))]
public sealed class CaptureOffHostCollection : ICollectionFixture<ProductionCaptureOffFixture>;
