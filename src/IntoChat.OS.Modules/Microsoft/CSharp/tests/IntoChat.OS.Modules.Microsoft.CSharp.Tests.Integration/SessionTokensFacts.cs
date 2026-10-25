using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Modules.Microsoft.CSharp;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class SessionTokensFacts(CSharpHost host)
{
    [Fact]
    public async Task SessionTokenInfrastructureIsNotAUserConnector()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var response = await brain.HttpClient.GetAsync("/connectors", ct);
        response.EnsureSuccessStatusCode();
        using var catalog = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.DoesNotContain(catalog.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == "csharp");
        using (brain.Owner.AsCaller())
        {
            // No connection neuron serves "csharp", so a connection key naming it addresses nothing.
            var key = IntoChat.OS.Connections.Platform.Connections.ConnectionKeys.For(IntoChat.OS.Framework.BrainScope.CurrentId(), "csharp", "internal");
            var error = Assert.Throws<ArgumentException>(() => IntoChat.OS.Connections.Platform.Connections.ConnectionGrains
                .GetNeuronGrain<IntoChat.OS.Connections.Platform.Connections.IConnection>(brain.Services.GetRequiredService<IGrainFactory>(), brain.Services, key));
            Assert.Contains("No connection neuron serves connector 'csharp'", error.Message);
        }
    }

    [Fact]
    public async Task AnIssuedTokenValidatesToItsBrainAndSession()
    {
        var tokens = await Tokens(null, TimeProvider.System);
        var claims = tokens.Validate(tokens.Issue("acc-brain", "s1"));
        Assert.Equal(("acc-brain", "s1"), (claims!.Brain, claims.Session));
    }

    [Fact]
    public async Task AnExpiredTokenIsNull()
    {
        var time = new MovableTime();
        var tokens = await Tokens(null, time);
        var token = tokens.Issue("acc-brain", "s1");
        time.Now += SessionTokens.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(tokens.Validate(token));
    }

    [Fact]
    public async Task ATamperedTokenIsNull()
    {
        var tokens = await Tokens(null, TimeProvider.System);
        var token = tokens.Issue("acc-brain", "s1");
        Assert.Null(tokens.Validate(token[..^2] + "zz"));
        Assert.Null(tokens.Validate("not.a-token"));
    }

    [Fact]
    public async Task SilosSharingAKeyAcceptEachOthersTokensAndNoOtherKeyDoes()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var issuer = await Tokens(key, TimeProvider.System);
        var peer = await Tokens(key, TimeProvider.System);
        var stranger = await Tokens(null, TimeProvider.System);

        var token = issuer.Issue("acc-brain", "s1");

        Assert.NotNull(peer.Validate(token));
        Assert.Null(stranger.Validate(token));
    }

    [Fact]
    public async Task ProductionRefusesAnUnprovisionedKey()
    {
        var tokens = new SessionTokens(new Handles(null), TimeProvider.System, new Environment { EnvironmentName = Environments.Production }, NullLogger<SessionTokens>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.InitializeAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<SessionTokens> Tokens(string? key, TimeProvider time)
    {
        var tokens = new SessionTokens(new Handles(key), time, new Environment(), NullLogger<SessionTokens>.Instance);
        await tokens.InitializeAsync(TestContext.Current.CancellationToken);
        return tokens;
    }

    private sealed class Handles(string? key) : IModuleIntegrations<CSharpModule>, IIntegrationHandle
    {
        public string IntegrationId => "csharp";
        public IIntegrationHandle Get(string id) => id == IntegrationId ? this : throw new UnauthorizedAccessException();
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = key is null ? IntegrationStatus.Unconfigured : IntegrationStatus.Ready });
        public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => Task.FromResult(key!);
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class MovableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
