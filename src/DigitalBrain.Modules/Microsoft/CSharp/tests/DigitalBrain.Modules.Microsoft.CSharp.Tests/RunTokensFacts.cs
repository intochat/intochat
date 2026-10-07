using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Edge;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class RunTokensFacts
{
    [Fact]
    public async Task RunTokenInfrastructureIsNotAUserConnector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await DigitalBrain.Testing.Module.ModuleTest.Create().WithModule<CSharpModule>().WithHttpEdge().StartAsync(ct);
        var response = await brain.HttpClient.GetAsync("/connectors", ct);
        response.EnsureSuccessStatusCode();
        using var catalog = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.DoesNotContain(catalog.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == "csharp");
        DigitalBrain.Framework.CallerContextStamper.Stamp(new()
        {
            PrincipalId = "owner",
            AccountId = "owner",
            BrainId = "owner",
            Kind = DigitalBrain.Framework.CallerKind.User,
            StampedBy = DigitalBrain.Framework.TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            // No connection neuron serves "csharp", so a connection key naming it addresses nothing.
            var key = DigitalBrain.Framework.Platform.Connections.ConnectionKeys.For(DigitalBrain.Framework.BrainScope.CurrentId(), "csharp", "internal");
            var error = Assert.Throws<ArgumentException>(() => DigitalBrain.Framework.Platform.Connections.ConnectionGrains
                .GetNeuronGrain<DigitalBrain.Framework.Platform.Connections.IConnection>(brain.Grains, brain.SiloServices, key));
            Assert.Contains("No connection neuron serves connector 'csharp'", error.Message);
        }
        finally { Orleans.Runtime.RequestContext.Remove(DigitalBrain.Framework.CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task ATokenNamesItsFileAndRunUntilItExpires()
    {
        var time = new MovableTime();
        var tokens = await CreateTokens(null, time);

        var token = tokens.Issue("workspace/report", "run-1");

        Assert.Equal(("workspace/report", "run-1"), (tokens.Validate(token)!.File, tokens.Validate(token)!.Run));
        time.Now += RunTokens.Lifetime;
        Assert.Null(tokens.Validate(token));
    }

    [Fact]
    public async Task SilosSharingAKeyAcceptEachOthersTokensAndNoOtherKeyDoes()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var issuer = await CreateTokens(key, TimeProvider.System);
        var peer = await CreateTokens(key, TimeProvider.System);
        var stranger = await CreateTokens(null, TimeProvider.System);

        var token = issuer.Issue("f", "r");

        Assert.NotNull(peer.Validate(token));
        Assert.Null(stranger.Validate(token));
        Assert.Null(issuer.Validate("not.a-token"));
    }

    [Fact]
    public async Task ProductionRefusesAnUnprovisionedKey()
    {
        var tokens = new RunTokens(new Handles(null), TimeProvider.System, new Environment { EnvironmentName = Environments.Production }, NullLogger<RunTokens>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.InitializeAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<RunTokens> CreateTokens(string? key, TimeProvider time)
    {
        var tokens = new RunTokens(new Handles(key), time, new Environment(), NullLogger<RunTokens>.Instance);
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
