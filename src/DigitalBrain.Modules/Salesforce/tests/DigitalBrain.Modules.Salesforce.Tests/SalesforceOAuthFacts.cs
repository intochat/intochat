using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Salesforce;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Modules.Salesforce.Tests;

public sealed class SalesforceOAuthFacts
{
    [Fact]
    public async Task RequestCredentialsAreReleasedAsynchronouslyWithoutMutatingSharedOAuthOptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var secret = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new SalesforceOAuthConfiguration(new Bindings(new Handle(secret.Task)),
            Options.Create(new SalesforceModuleOptions { PublicOrigin = new Uri("https://example.test") }));
        var shared = new SalesforceOAuthHandlerOptions();
        SalesforceOAuthConfiguration.Configure(shared);
        var request = shared.Copy();
        var pending = settings.ConfigureAsync(request, ct);
        Assert.False(pending.IsCompleted);
        secret.SetResult("test-secret");
        await pending;
        Assert.Equal("test-key", request.ClientId);
        Assert.Equal("test-secret", request.ClientSecret);
        Assert.Equal("not-configured", shared.ClientId);
        Assert.Equal("not-configured", shared.ClientSecret);
    }

    [Fact]
    public async Task RefreshReadsCurrentIntegrationCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new SalesforceOAuthConfiguration(new Bindings(new Handle(Task.FromResult("rotated-secret"))),
            Options.Create(new SalesforceModuleOptions { PublicOrigin = new Uri("https://example.test") }));
        using var request = await settings.RefreshRequestAsync("refresh-token", ct);
        var body = await request.Content!.ReadAsStringAsync(ct);
        Assert.Contains("client_secret=rotated-secret", body);
        Assert.Contains("client_id=test-key", body);
    }

    [Fact]
    public async Task CredentialsUseTheSingleCoherentReleaseInsteadOfSeparateFieldReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var material = new TaskCompletionSource<ReleasedIntegration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new SalesforceOAuthConfiguration(new Bindings(new AtomicHandle(material.Task)), Options.Create(new SalesforceModuleOptions { PublicOrigin = new Uri("https://example.test") }));
        var request = new SalesforceOAuthHandlerOptions();
        var pending = settings.ConfigureAsync(request, ct);
        Assert.False(pending.IsCompleted);
        material.SetResult(new() { Snapshot = new() { IntegrationId = "salesforce", Status = IntegrationStatus.Ready }, Values = new() { ["ConsumerKey"] = "rotated-key", ["ConsumerSecret"] = "rotated-secret" } });
        await pending;
        Assert.Equal("rotated-key", request.ClientId);
        Assert.Equal("rotated-secret", request.ClientSecret);
    }
    private sealed class AtomicHandle(Task<ReleasedIntegration> material) : IIntegrationHandle
    {
        public string IntegrationId => "salesforce";
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Separate reads mix revisions.");
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default) => material.WaitAsync(cancellationToken);
    }

    private sealed class Bindings(IIntegrationHandle handle) : IModuleIntegrations<SalesforceModule>
    {
        public IIntegrationHandle Get(string integrationId) { Assert.Equal("salesforce", integrationId); return handle; }
    }
    private sealed class Handle(Task<string> secret) : IIntegrationHandle
    {
        public string IntegrationId => "salesforce";
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
            => new() { Snapshot = await ReadAsync(cancellationToken), Values = new() { ["ConsumerKey"] = await ReadSecretAsync("ConsumerKey", cancellationToken), ["ConsumerSecret"] = await ReadSecretAsync("ConsumerSecret", cancellationToken) } };
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
            => field == "ConsumerKey" ? Task.FromResult("test-key") : secret.WaitAsync(cancellationToken);
    }
}
