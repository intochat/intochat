using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Sdk.Platform.Integrations;
using IntoChat.OS.Modules.Google.Gmail;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The reference composition with Google stubbed on loopback: a configured Gmail OAuth client, its token
// exchange and profile on a TokenEndpointStub, so the Apps window's Connect completes for real on the script runtime.
// Salesforce stays unconfigured. Its own host: the shared reference host offers no OAuth client at all.
public sealed class ConnectHostFixture : ReferenceBrainFixture
{
    private TokenEndpointStub? _tokens;

    public TokenEndpointStub Tokens => _tokens ?? throw new InvalidOperationException("The host starts in InitializeAsync.");

    protected override async Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
    {
        _tokens = await TokenEndpointStub.StartAsync(cancellationToken);
        return await ReferenceBrain.Create(privateConfiguration: new()
        {
            [IntegrationSchema.Key(GmailModule.Integration.Id, "ClientId")] = "os-client",
            [IntegrationSchema.Key(GmailModule.Integration.Id, "ClientSecret")] = "os-secret",
            [IntoChatOSConfiguration.ConnectionsTokenEndpointKey("gmail")] = _tokens.TokenEndpoint.AbsoluteUri,
            [IntoChatOSConfiguration.ConnectionsPublicOriginKey] = _tokens.Origin.AbsoluteUri,
        })
            .WithOptions<GmailModule, GmailModuleOptions>(gmail => gmail.ProfileEndpoint = new(_tokens.Origin, "profile"))
            .WithOptions<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .WithStartupTimeout(TimeSpan.FromMinutes(15))
            .WithDashboardTelemetry()
            .StartAsync(cancellationToken);
    }

    protected override async ValueTask OnDisposedAsync()
    {
        await base.OnDisposedAsync();
        if (_tokens is not null) { await _tokens.DisposeAsync(); }
    }
}
