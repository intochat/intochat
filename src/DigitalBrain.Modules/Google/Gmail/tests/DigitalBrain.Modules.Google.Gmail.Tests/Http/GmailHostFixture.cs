using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Testing.Module;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

// One in-process brain with the Gmail module, a configured registration, a stubbed Google token
// endpoint and the product HTTP pipeline. The collection's facts share it; a fact that needs a
// different registration (for example an unconfigured one) starts its own brain — in-process,
// that costs seconds, not a container boot.
public sealed class GmailHostFixture : IAsyncLifetime
{
    private TokenEndpointStub? _stub;
    private ModuleBrain? _brain;

    public ModuleBrain Brain => _brain ?? throw new InvalidOperationException("The Gmail host has not started yet.");

    public async ValueTask InitializeAsync()
    {
        _stub = await TokenEndpointStub.StartAsync(CancellationToken.None);
        _brain = await ModuleTest.Create()
            .WithModule<GmailModule, GmailModuleOptions>(options => { options.TokenEndpoint = _stub.TokenEndpoint; options.ProfileEndpoint = new(_stub.Origin, "profile"); options.PublicOrigin = _stub.Origin; })
            .WithExecution(new() { PrivateConfiguration = ConfiguredRegistration() })
            .ConfigureSilo(silo => silo.Services.AddSingleton<IGmailPushAuthenticator>(new TestPushAuthenticator()))
            .WithHttpEdge()
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_brain is not null) { await _brain.DisposeAsync(); }
        if (_stub is not null) { await _stub.DisposeAsync(); }
    }

    internal async Task ConnectForPushAsync(string brainName, string connectionId)
    {
        var state = Brain.SiloServices.GetRequiredService<GmailCallbackRequests>().Begin("owner", "owner", brainName, connectionId);
        using var response = await Brain.HttpClient.GetAsync($"google/gmail/oauth/callback?code=fake-code&state={state}");
        response.EnsureSuccessStatusCode();
    }

    internal static Dictionary<string, string?> ConfiguredRegistration() => new()
    {
        [IntegrationSchema.Key("google-oauth", "ClientId")] = "integration-client",
        [IntegrationSchema.Key("google-oauth", "ClientSecret")] = "integration-secret",
    };
}

[CollectionDefinition(Name)]
public sealed class GmailHostCollection : ICollectionFixture<GmailHostFixture>
{
    public const string Name = "gmail-configured-host";
}

internal sealed class TestPushAuthenticator : IGmailPushAuthenticator
{
    public Task<bool> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken) => Task.FromResult(request.Headers.Authorization == "Bearer test-google-push");
}
