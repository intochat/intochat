using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Microsoft.GitHub;
using IntoChat.OS.Microsoft.GitHub.Webhook;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Integration;

public sealed class OAuthCredentialFacts
{
    [Fact]
    public async Task MissingOptionalOAuthCredentialsDoNotDisableInstallationAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        var registration = new GitHubAppRegistration(new Bindings(new Handle(Task.FromException<string>(new KeyNotFoundException()))));
        Assert.Equal(7, await registration.ReadAppIdAsync(ct));
        Assert.Null(await registration.ReadOptionalSecretAsync("ClientSecret", ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task WebhookRotationRejectsOldSignaturesAndAcceptsTheCurrentIntegrationSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        const string original = "original-webhook-secret";
        const string rotated = "rotated-webhook-secret";
        await using var brain = await TestBrain.Create().WithModule<GitHubModule>()
            .WithIntegration(GitHubModule.Integration, "AppId", "7")
            .WithIntegration(GitHubModule.Integration, "PrivateKeyPem", "test-key-not-used-by-webhooks")
            .WithIntegration(GitHubModule.Integration, "WebhookSecret", original)
            .StartAsync(ct);
        var ingress = brain.Services.GetRequiredService<GitHubWebhookIngress>();
        var body = Encoding.UTF8.GetBytes("{\"hook\":{\"app_id\":7}}");
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await ingress.HandleAsync(body, Headers(body, original), ct));
        await Grains(brain).GetGrain<IIntegration>("integration/github").Configure(new() { Values = { ["WebhookSecret"] = rotated } });
        Assert.Equal(GitHubWebhookAcceptance.Unauthorized, await ingress.HandleAsync(body, Headers(body, original), ct));
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await ingress.HandleAsync(body, Headers(body, rotated), ct));
    }

    private static IGrainFactory Grains(ITestBrain brain) => brain.Services.GetRequiredService<IGrainFactory>();

    private static Dictionary<string, string[]> Headers(byte[] body, string secret) => new()
    {
        ["X-GitHub-Delivery"] = [Guid.NewGuid().ToString()],
        ["X-GitHub-Event"] = ["ping"],
        ["X-Hub-Signature-256"] = ["sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body))],
    };

    [Fact]
    public async Task AppIdentityAndSigningKeyComeFromTheSameRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var material = new TaskCompletionSource<ReleasedIntegration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new GitHubAppRegistration(new Bindings(new AtomicHandle(material.Task)));
        var pending = registration.ReleaseAsync(ct);
        Assert.False(pending.IsCompleted);
        material.SetResult(new() { Snapshot = new() { IntegrationId = "github", Status = IntegrationStatus.Ready, Settings = new() { ["AppId"] = "9" } }, Values = new() { ["PrivateKeyPem"] = "rotated-key" } });
        var credentials = await pending;
        Assert.Equal(9, credentials.AppId);
        Assert.Equal("rotated-key", credentials.PrivateKeyPem);
    }
    private sealed class AtomicHandle(Task<ReleasedIntegration> material) : IIntegrationHandle
    {
        public string IntegrationId => "github";
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready, Settings = new() { ["AppId"] = "7" } });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Separate reads mix revisions.");
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default) => material.WaitAsync(cancellationToken);
    }

    private sealed class Bindings(IIntegrationHandle handle) : IModuleIntegrations<GitHubModule>
    {
        public IIntegrationHandle Get(string integrationId) { Assert.Equal("github", integrationId); return handle; }
    }
    private sealed class Handle(Task<string> secret) : IIntegrationHandle
    {
        public string IntegrationId => "github";
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready, Settings = new() { ["AppId"] = "7" } });
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
            => new() { Snapshot = await ReadAsync(cancellationToken), Values = new() { ["ClientSecret"] = await ReadSecretAsync("ClientSecret", cancellationToken) } };
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => secret.WaitAsync(cancellationToken);
    }
}
