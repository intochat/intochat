using System.Text;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Gmail.Testing;

// A brain with the Gmail module whose Google is the token/profile stub.
public static class GmailBrain
{
    public const string PushToken = "test-google-push";

    public static Task<ITestBrain> StartAsync(GoogleStub stub, CancellationToken ct, string? posture = null, Uri? drive = null, bool watch = false)
        => Compose(TestBrain.Create(), stub, posture, drive, watch).StartAsync(ct);

    public static TestBrain Compose(TestBrain brain, GoogleStub stub, string? posture = null, Uri? drive = null, bool watch = false)
    {
        var builder = brain
            .WithModule<GmailModule, GmailModuleOptions>(options =>
            {
                options.ProfileEndpoint = stub.ProfileEndpoint;
                options.GmailEndpoint = stub.GmailEndpoint;
                if (drive is not null) { options.DriveEndpoint = drive; }
            })
            .WithIntegration(GmailModule.Integration, "ClientId", "integration-client")
            .WithIntegration(GmailModule.Integration, "ClientSecret", "integration-secret")
            .WithConfiguration(IntoChatOSConfiguration.ConnectionsPublicOriginKey, stub.Origin.AbsoluteUri)
            .WithConfiguration(IntoChatOSConfiguration.ConnectionsTokenEndpointKey("gmail"), stub.TokenEndpoint.AbsoluteUri)
            // Google signs push deliveries with its own OIDC keys; the test cannot mint those, so the push gate accepts a fixed bearer.
            .ConfigureServices(services =>
            {
                services.AddSingleton(stub);
                services.AddSingleton<IGmailPushAuthenticator>(new TestPushAuthenticator());
            });
        if (watch)
        {
            var pubSub = GmailModule.Integrations.Single(schema => schema.Id == "google-pubsub");
            builder = builder.WithIntegration(pubSub, "Audience", "https://push.example.test/gmail")
                .WithIntegration(pubSub, "ServiceAccountEmail", "push@example.test")
                .WithIntegration(pubSub, "TopicName", "projects/example/topics/gmail");
        }
        if (posture is not null) { builder = builder.WithConfiguration(IntoChat.OS.Connections.Identity.Configuration.AuthOptions.PostureKey, posture); }
        return builder;
    }

    // Gmail's inbound grain behind the edge is not a neuron; the brain's grain factory reaches it.
    public static IGrainFactory Grains(this ITestBrain brain) => brain.Services.GetRequiredService<IGrainFactory>();

    // The Platform's browser login: start it, then return through its callback with a code.
    public static async Task ConnectAsync(this Person person, string connectionId, CancellationToken ct, string authMethodId = "google-oauth")
    {
        using var start = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/connections/{connectionId}/login", new { connectorId = "gmail", authMethodId }, ct);
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

internal sealed class TestPushAuthenticator : IGmailPushAuthenticator
{
    public Task<bool> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken) => Task.FromResult(request.Headers.Authorization == $"Bearer {GmailBrain.PushToken}");
}
