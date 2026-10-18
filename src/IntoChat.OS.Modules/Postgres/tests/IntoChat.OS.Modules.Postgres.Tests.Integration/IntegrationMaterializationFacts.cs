using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Postgres;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration;

public sealed class IntegrationMaterializationFacts
{
    [Theory]
    [InlineData(IntegrationStatus.Partial)]
    [InlineData(IntegrationStatus.Unconfigured)]
    public async Task RepeatedReadinessChecksReuseTheClientWhileRotationAndRevocationTakeEffect(IntegrationStatus revokedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var integration = new ChangingIntegration();
        await using var provider = new PostgresSourceRegistry(integration, Options.Create(new PostgresModuleOptions()));
        var initial = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.Get(IntoChat.OS.Postgres.Tables.TablePlacement.Shared, ct)));
        Assert.All(initial, value => Assert.Same(initial[0], value));
        Assert.Equal(8, integration.Reads);
        Assert.Equal(1, integration.Releases);

        integration.Revision = 2;
        integration.RotateDuringRelease = true;
        var rotated = await provider.Get(IntoChat.OS.Postgres.Tables.TablePlacement.Shared, ct);
        Assert.NotSame(initial[0], rotated);
        Assert.Same(rotated, await provider.Get(IntoChat.OS.Postgres.Tables.TablePlacement.Shared, ct));
        Assert.Equal(3, integration.Revision);
        Assert.Equal(2, integration.Releases);

        integration.Status = revokedStatus;
        await Assert.ThrowsAsync<PostgresUnavailableException>(() => provider.Get(IntoChat.OS.Postgres.Tables.TablePlacement.Shared, ct));
        Assert.Equal(2, integration.Releases);
        integration.Status = IntegrationStatus.Ready;
        integration.Revision = 4;
        Assert.NotSame(rotated, await provider.Get(IntoChat.OS.Postgres.Tables.TablePlacement.Shared, ct));
        Assert.Equal(3, integration.Releases);
    }

    private sealed class ChangingIntegration : IModuleIntegrations<PostgresModule>, IIntegrationHandle
    {
        public string IntegrationId => "postgres";
        public long Revision { get; set; } = 1;
        public IntegrationStatus Status { get; set; } = IntegrationStatus.Ready;
        public bool RotateDuringRelease { get; set; }
        public int Reads { get; private set; }
        public int Releases { get; private set; }
        public IIntegrationHandle Get(string integrationId) { Assert.Equal(IntegrationId, integrationId); return this; }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Snapshot());
        }
        private IntegrationSnapshot Snapshot() => new() { IntegrationId = IntegrationId, Revision = Revision, Status = Status };
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Releases++;
            await Task.Yield();
            if (RotateDuringRelease) { Revision++; RotateDuringRelease = false; }
            return new ReleasedIntegration
            {
                Snapshot = Snapshot(),
                Values = new() { ["ConnectionString"] = $"Host=localhost;Database=revision_{Revision};Username=reader" },
            };
        }
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Materialization must use one atomic release.");
    }
}
