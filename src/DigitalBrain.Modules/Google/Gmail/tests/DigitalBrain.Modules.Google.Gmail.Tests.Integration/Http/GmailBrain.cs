using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Google.Gmail.Tests.Integration.GmailHost))]

namespace DigitalBrain.Modules.Google.Gmail.Tests.Integration;

// A brain with the Gmail module whose Google is the token/profile stub.
internal static class GmailBrain
{
    public const string PushToken = "test-google-push";

    public static Task<ITestBrain> StartAsync(TokenEndpointStub stub, CancellationToken ct, string? posture = null)
        => Compose(TestBrain.Create(), stub, posture).StartAsync(ct);

    public static TestBrain Compose(TestBrain brain, TokenEndpointStub stub, string? posture = null)
    {
        stub.Scope = "https://www.googleapis.com/auth/gmail.readonly";
        var builder = brain
            .WithModule<GmailModule, GmailModuleOptions>(options => options.ProfileEndpoint = new(stub.Origin, "profile"))
            .WithIntegration(GmailModule.Integration, "ClientId", "integration-client")
            .WithIntegration(GmailModule.Integration, "ClientSecret", "integration-secret")
            .WithConfiguration(DigitalBrainConfiguration.ConnectionsPublicOriginKey, stub.Origin.AbsoluteUri)
            .WithConfiguration(DigitalBrainConfiguration.ConnectionsTokenEndpointKey("gmail"), stub.TokenEndpoint.AbsoluteUri)
            // Google signs push deliveries with its own OIDC keys; the test cannot mint those, so the push gate accepts a fixed bearer.
            .ConfigureServices(services => services.AddSingleton<IGmailPushAuthenticator>(new TestPushAuthenticator()));
        if (posture is not null) { builder = builder.WithConfiguration(DigitalBrain.Platform.Identity.Configuration.AuthOptions.PostureKey, posture); }
        return builder;
    }

    // Gmail's inbound grain behind the edge is not a neuron; the brain's grain factory reaches it.
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

    // The Pub/Sub push subscription URL names the brain it delivers to.
    public static async Task<HttpResponseMessage> PushAsync(this ITestBrain brain, string brainId, string emailAddress, string historyId, CancellationToken ct, string bearer = PushToken)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { emailAddress, historyId })));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GmailModule.WatchPath}/{brainId}")
        {
            Content = JsonContent.Create(new { message = new { data, messageId = historyId }, subscription = "projects/x/subscriptions/gmail" }),
        };
        request.Headers.Authorization = new("Bearer", bearer);
        return await brain.HttpClient.SendAsync(request, ct);
    }
}

// One brain shared by the assembly over one token/profile stub; each fact works in its own person's brain or connection.
public sealed class GmailHost : BrainHostFixture
{
    private TokenEndpointStub? _stub;

    protected override TestBrain Configure(TestBrain brain)
        => GmailBrain.Compose(brain, _stub ?? throw new InvalidOperationException("The stub starts first."));

    public override async ValueTask InitializeAsync()
    {
        _stub = await TokenEndpointStub.StartAsync(CancellationToken.None);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_stub is { } stub) { await stub.DisposeAsync(); }
    }
}

internal sealed class TestPushAuthenticator : IGmailPushAuthenticator
{
    public Task<bool> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken) => Task.FromResult(request.Headers.Authorization == $"Bearer {GmailBrain.PushToken}");
}
