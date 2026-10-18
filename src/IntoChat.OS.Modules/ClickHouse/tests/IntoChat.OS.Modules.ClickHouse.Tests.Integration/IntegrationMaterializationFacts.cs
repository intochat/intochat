using IntoChat.OS.ClickHouse;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.ClickHouse.Tests.Integration;

public sealed class IntegrationMaterializationFacts
{
    [Theory]
    [InlineData(IntegrationStatus.Partial)]
    [InlineData(IntegrationStatus.Unconfigured)]
    public async Task RepeatedReadinessChecksReuseTheClientWhileRotationAndRevocationTakeEffect(IntegrationStatus revokedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var integration = new ChangingIntegration();
        using var clients = new Clients();
        var provider = new IntegrationClickHouseProvider(integration, clients);
        var initial = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.Materialize(ct)));
        Assert.All(initial, value => Assert.Same(initial[0], value));
        Assert.Equal(8, integration.Reads);
        Assert.Equal(1, integration.Releases);

        integration.Revision = 2;
        integration.RotateDuringRelease = true;
        var rotated = await provider.Materialize(ct);
        Assert.NotSame(initial[0], rotated);
        Assert.Same(rotated, await provider.Materialize(ct));
        Assert.Equal(3, integration.Revision);
        Assert.Equal(2, integration.Releases);

        integration.Status = revokedStatus;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Materialize(ct));
        Assert.Equal(2, integration.Releases);
        integration.Status = IntegrationStatus.Ready;
        integration.Revision = 4;
        Assert.NotSame(rotated, await provider.Materialize(ct));
        Assert.Equal(3, integration.Releases);
    }

    private sealed class ChangingIntegration : IModuleIntegrations<ClickHouseModule>, IIntegrationHandle
    {
        public string IntegrationId => "clickhouse";
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

    private sealed class Clients : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new();
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }
}
