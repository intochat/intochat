namespace IntoChat.OS.Framework.Tests.E2E;

// Every host here boots the IntoChat.OS AppHost, whose RunProfile.Apply hosts ArtCraft (WithHostedApps): persistent,
// unproxied containers on fixed host ports (ArtCraftModuleOptions PhotoCraft/FilmCraft Mcp and Web ports). Two AppHosts
// booting at once would race for those containers and ports, so each AppHost collection disables parallelization: they run
// one after another, each host still booting only when its collection is selected and disposed before the next.
[CollectionDefinition(nameof(ReferenceBrainFixture), DisableParallelization = true)]
public sealed class ReferenceHostCollection : ICollectionFixture<ReferenceBrainFixture>;

[CollectionDefinition(nameof(ProductionBrainFixture), DisableParallelization = true)]
public sealed class ProductionHostCollection : ICollectionFixture<ProductionBrainFixture>;

[CollectionDefinition(nameof(ProductionCaptureOffFixture), DisableParallelization = true)]
public sealed class CaptureOffHostCollection : ICollectionFixture<ProductionCaptureOffFixture>;

// ProductionCaptureOffFixture differs from ProductionBrainFixture only by AIOptions.Telemetry.EnableSensitiveData, a module
// option the AppHost bakes into the silo at boot: it is host-global, so the two cannot share one host with a per-fact flag.

// Span budgets count a whole host's exported spans: streamed chat reconciles and idle timers vary with CPU contention, so
// these measurements run on their own host, which is serial like every AppHost collection above.
[CollectionDefinition("Span budget", DisableParallelization = true)]
public sealed class SpanBudgetCollection : ICollectionFixture<ReferenceBrainFixture>;
