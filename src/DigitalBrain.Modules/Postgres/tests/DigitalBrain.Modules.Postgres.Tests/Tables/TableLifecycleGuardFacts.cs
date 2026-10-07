using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Postgres.Tests.Tables;

public sealed class TableLifecycleGuardFacts
{
    [Fact]
    public async Task AnIndexOfAnotherBrainRefusesEveryCallerAndAnAppCallerCannotReadItsOwnIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<PostgresModule>().ConfigureSilo(silo =>
        {
            silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=platform;Username=reader";
            silo.Services.AddSingleton<ITableStore>(new PostgresWriteTableFacts.MemoryTables());
        }).StartAsync(ct);
        CallerContextStamper.Stamp(new()
        {
            PrincipalId = "alice", AccountId = "alice", BrainId = "brain",
            Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform,
        });
        await brain.AuthorizeCallerAsync();
        var foreign = brain.Get<ITableIndex>(new TableOwner("other-brain", "install").Key);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(foreign.ReadTables);
        var malformed = brain.Get<ITableIndex>("not a key");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(malformed.ReadTables);

        CallerContextStamper.Stamp(new()
        {
            PrincipalId = "alice", AccountId = "alice", BrainId = "brain",
            Kind = CallerKind.App, AppId = "install", StampedBy = TrustedEdge.AppProxy,
        });
        await brain.AuthorizeCallerAsync();
        var own = brain.Get<ITableIndex>(new TableOwner(BrainScope.CurrentId(), "install").Key);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(own.ReadTables);
    }
}
