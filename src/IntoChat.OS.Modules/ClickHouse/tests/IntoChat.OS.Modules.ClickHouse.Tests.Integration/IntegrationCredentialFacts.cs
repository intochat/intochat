using IntoChat.OS.ClickHouse;
using IntoChat.OS.Framework.Platform.Integrations;

namespace IntoChat.OS.Modules.ClickHouse.Tests.Integration;

public sealed class IntegrationCredentialFacts
{
    [Fact]
    public async Task ProviderReadsOnlyItsHandleAndPropagatesCancellationBeforeConstructingAClient()
    {
        var integration = new MissingIntegration();
        var provider = new IntegrationClickHouseProvider(integration, null!);
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.QueryAsync("select 1", 1, ct));
        Assert.Equal(("clickhouse", "Release", ct), integration.Request);
    }

    private sealed class MissingIntegration : IModuleIntegrations<ClickHouseModule>, IIntegrationHandle
    {
        public string IntegrationId => "clickhouse";
        public (string, string, CancellationToken) Request { get; private set; }
        public IIntegrationHandle Get(string integrationId)
        {
            Assert.Equal(IntegrationId, integrationId);
            return this;
        }
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Request = (IntegrationId, "Release", cancellationToken);
            await Task.CompletedTask;
            throw new InvalidOperationException("Integration unavailable.");
        }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
        {
            Request = (IntegrationId, field, cancellationToken);
            throw new InvalidOperationException("Integration unavailable.");
        }
    }
}
