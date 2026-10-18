namespace DigitalBrain.Framework.Tests.E2E;

[CollectionDefinition(nameof(ReferenceBrainFixture))]
public sealed class ReferenceHostCollection : ICollectionFixture<ReferenceBrainFixture>;

[CollectionDefinition(nameof(ProductionBrainFixture))]
public sealed class ProductionHostCollection : ICollectionFixture<ProductionBrainFixture>;

[CollectionDefinition(nameof(ProductionCaptureOffFixture))]
public sealed class CaptureOffHostCollection : ICollectionFixture<ProductionCaptureOffFixture>;

// ProductionCaptureOffFixture differs from ProductionBrainFixture only by AIOptions.Telemetry.EnableSensitiveData, a module
// option the AppHost bakes into the silo at boot: it is host-global, so the two cannot share one host with a per-fact flag.

// Span budgets count a whole host's exported spans: streamed chat reconciles and idle timers vary with CPU contention, so
// these measurements run on their own host, alone, after the parallel collections.
[CollectionDefinition("Span budget", DisableParallelization = true)]
public sealed class SpanBudgetCollection : ICollectionFixture<ReferenceBrainFixture>;
