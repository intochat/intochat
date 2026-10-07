using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Testing.Module;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
    internal TokenEndpointStub Stub => _stub ?? throw new InvalidOperationException("The Gmail host has not started yet.");

    public async ValueTask InitializeAsync()
    {
        _stub = await TokenEndpointStub.StartAsync(CancellationToken.None);
        _brain = await ModuleTest.Create()
            .WithModule<GmailModule, GmailModuleOptions>(options => options.ProfileEndpoint = new(_stub.Origin, "profile"))
            .WithExecution(new()
            {
                PrivateConfiguration = new Dictionary<string, string?>(ConfiguredRegistration())
                {
                    [DigitalBrainConfiguration.ConnectionsPublicOriginKey] = _stub.Origin.AbsoluteUri,
                    [DigitalBrainConfiguration.ConnectionsTokenEndpointKey("gmail")] = _stub.TokenEndpoint.AbsoluteUri,
                },
            })
            .ConfigureSilo(silo => silo.Services.AddSingleton<IGmailPushAuthenticator>(new TestPushAuthenticator()))
            .WithHttpEdge()
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_brain is not null) { await _brain.DisposeAsync(); }
        if (_stub is not null) { await _stub.DisposeAsync(); }
    }

    // The Platform's browser login: start it, then return through its callback with a code.
    internal async Task ConnectForPushAsync(string brainName, string connectionId)
    {
        using var start = await Brain.HttpClient.PostAsJsonAsync($"/brains/{brainName}/connections/{connectionId}/login", new { connectorId = "gmail", authMethodId = "google-oauth" });
        start.EnsureSuccessStatusCode();
        var url = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!);
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
        using var callback = await Brain.HttpClient.GetAsync($"/connections/oauth/gmail/callback?code=fake-code&state={state}");
        callback.EnsureSuccessStatusCode();
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
