namespace IntoChat.OS.Kernel.Tests.E2E;

// Every host here boots the IntoChat.OS AppHost, whose RunProfile.Apply hosts ArtCraft (WithHostedApps): persistent,
// unproxied containers on fixed host ports (ArtCraftModuleOptions PhotoCraft/FilmCraft Mcp and Web ports). Two AppHosts
// booting at once would race for those containers and ports, so each AppHost collection disables parallelization: they run
// one after another, each host still booting only when its collection is selected and disposed before the next.
// Filtered app runs start just the reference host.
[CollectionDefinition("Reference host", DisableParallelization = true)]
public sealed class ReferenceHostCollection : ICollectionFixture<ReferenceBrainFixture>;

[CollectionDefinition("Product host", DisableParallelization = true)]
public sealed class ProductHostCollection : ICollectionFixture<ProductHostFixture>;

[CollectionDefinition("Router host", DisableParallelization = true)]
public sealed class RouterHostCollection : ICollectionFixture<RouterHostFixture>;

// Self-improvement builds a candidate in Docker and rolls it beside the cluster through the machine's one Aspire CLI
// cluster host: machine-wide state no other host may share; it boots its own AppHost too, so it runs serially as well.
[CollectionDefinition("Candidate host", DisableParallelization = true)]
public sealed class CandidateHostCollection;
