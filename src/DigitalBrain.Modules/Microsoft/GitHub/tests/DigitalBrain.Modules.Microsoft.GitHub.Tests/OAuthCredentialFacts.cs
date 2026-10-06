using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.GitHub;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.GitHub.Tests;

public sealed class OAuthCredentialFacts
{
    [Fact]
    public async Task OAuthOptionsAreClonedBeforeAsyncSecretRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var secret = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new GitHubAppRegistration(new Bindings(new Handle(secret.Task)));
        var settings = new GitHubOAuthConfiguration(Options.Create(new GitHubModuleOptions
        { ClientId = "public-client-id", PublicOrigin = "https://example.test" }), registration);
        var shared = new GitHubOAuthHandlerOptions { ClientId = "not-configured", ClientSecret = "not-configured" };
        var request = shared.Copy();
        var pending = settings.ConfigureAsync(request, ct);
        Assert.False(pending.IsCompleted);
        secret.SetResult("client-secret");
        await pending;
        Assert.Equal("public-client-id", request.ClientId);
        Assert.Equal("client-secret", request.ClientSecret);
        Assert.Equal("not-configured", shared.ClientId);
        Assert.Equal("not-configured", shared.ClientSecret);
    }

    [Fact]
    public async Task MissingOptionalOAuthCredentialsDoNotDisableInstallationAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        var registration = new GitHubAppRegistration(new Bindings(new Handle(Task.FromException<string>(new KeyNotFoundException()))));
        Assert.Equal(7, await registration.ReadAppIdAsync(ct));
        Assert.Null(await registration.ReadOptionalSecretAsync("ClientSecret", ct));
    }

    [Fact]
    public async Task WebhookRotationRejectsOldSignaturesAndAcceptsTheCurrentIntegrationSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        const string original = "original-webhook-secret";
        const string rotated = "rotated-webhook-secret";
        await using var brain = await ModuleTest.Create().WithExecution(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?>
            {
                [IntegrationSchema.Key("github", "AppId")] = "7",
                [IntegrationSchema.Key("github", "PrivateKeyPem")] = "test-key-not-used-by-webhooks",
                [IntegrationSchema.Key("github", "WebhookSecret")] = original,
            },
        }).WithModule<GitHubModule>().ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton(new GitHubRepositoryBindings([new("repo", 11, 9, 7, "intochat", "digitalbrain")]));
            silo.Services.AddSingleton<IGitHubRepositorySource>(new FakeGitHubRepositorySource());
        }).StartAsync(ct);
        await brain.Get<IRepository>("repo").Connect(new(7, 9, 11, "intochat", "digitalbrain"));
        var ingress = brain.SiloServices.GetRequiredService<GitHubWebhookIngress>();
        var body = Encoding.UTF8.GetBytes("{\"hook\":{\"app_id\":7}}");
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await ingress.HandleAsync(body, Headers(body, original), ct));
        await brain.Get<IIntegration>("integration/github").Configure(new() { Values = { ["WebhookSecret"] = rotated } });
        Assert.Equal(GitHubWebhookAcceptance.Unauthorized, await ingress.HandleAsync(body, Headers(body, original), ct));
        Assert.Equal(GitHubWebhookAcceptance.Accepted, await ingress.HandleAsync(body, Headers(body, rotated), ct));
    }

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
