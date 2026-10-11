using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Google.Gmail.Tests;

// A brain with the Gmail module whose Google is the token/profile stub.
internal static class GmailBrain
{
    public const string PushToken = "test-google-push";

    public static Task<ITestBrain> StartAsync(TokenEndpointStub stub, CancellationToken ct, string? posture = null)
    {
        var builder = TestBrain.Create()
            .WithModule<GmailModule, GmailModuleOptions>(options => options.ProfileEndpoint = new(stub.Origin, "profile"))
            .WithIntegration(GmailModule.Integration, "ClientId", "integration-client")
            .WithIntegration(GmailModule.Integration, "ClientSecret", "integration-secret")
            .WithConfiguration(DigitalBrainConfiguration.ConnectionsPublicOriginKey, stub.Origin.AbsoluteUri)
            .WithConfiguration(DigitalBrainConfiguration.ConnectionsTokenEndpointKey("gmail"), stub.TokenEndpoint.AbsoluteUri)
            // Google signs push deliveries with its own OIDC keys; the test cannot mint those, so the push gate accepts a fixed bearer.
            .ConfigureServices(services => services.AddSingleton<IGmailPushAuthenticator>(new TestPushAuthenticator()));
        if (posture is not null) { builder = builder.WithConfiguration(DigitalBrain.Platform.Identity.Configuration.AuthOptions.PostureKey, posture); }
        return builder.StartAsync(ct);
    }

    // Gmail's grains behind the edge (inbound, mailbox index) are not neurons; the brain's grain factory reaches them.
    public static IGrainFactory Grains(this ITestBrain brain) => brain.Services.GetRequiredService<IGrainFactory>();

    // The Platform's browser login: start it, then return through its callback with a code.
    public static async Task ConnectAsync(this Person person, string connectionId, CancellationToken ct)
    {
        using var start = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/connections/{connectionId}/login", new { connectorId = "gmail", authMethodId = "google-oauth" }, ct);
        start.EnsureSuccessStatusCode();
        var url = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
        using var callback = await person.Http.GetAsync($"/connections/oauth/gmail/callback?code=fake-code&state={state}", ct);
        callback.EnsureSuccessStatusCode();
    }

    public static async Task<HttpResponseMessage> PushAsync(this ITestBrain brain, string emailAddress, string historyId, CancellationToken ct, string bearer = PushToken)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { emailAddress, historyId })));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/google/gmail/watch")
        {
            Content = JsonContent.Create(new { message = new { data, messageId = historyId }, subscription = "projects/x/subscriptions/gmail" }),
        };
        request.Headers.Authorization = new("Bearer", bearer);
        return await brain.HttpClient.SendAsync(request, ct);
    }
}

// One brain shared by the collection; each fact works in its own person's brain or connection.
public sealed class GmailHost : IAsyncLifetime
{
    private TokenEndpointStub? _stub;
    private ITestBrain? _brain;

    public ITestBrain Brain => _brain ?? throw new InvalidOperationException("The Gmail host has not started yet.");

    public async ValueTask InitializeAsync()
    {
        _stub = await TokenEndpointStub.StartAsync(CancellationToken.None);
        _brain = await GmailBrain.StartAsync(_stub, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_brain is not null) { await _brain.DisposeAsync(); }
        if (_stub is not null) { await _stub.DisposeAsync(); }
    }
}

[CollectionDefinition(Name)]
public sealed class GmailHostCollection : ICollectionFixture<GmailHost>
{
    public const string Name = "gmail-host";
}

internal sealed class TestPushAuthenticator : IGmailPushAuthenticator
{
    public Task<bool> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken) => Task.FromResult(request.Headers.Authorization == $"Bearer {GmailBrain.PushToken}");
}
