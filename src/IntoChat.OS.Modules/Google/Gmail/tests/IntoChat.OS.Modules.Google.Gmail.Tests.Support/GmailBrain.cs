using System.Text;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.Google.Auth;
using IntoChat.OS.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Gmail.Testing;

// A brain with the Gmail module whose Google is the token/profile stub.
public static class GmailBrain
{
    public const string PushToken = "test-google-push";

    public static Task<ITestBrain> StartAsync(GoogleStub stub, CancellationToken ct, string? posture = null, bool watch = false)
        => Compose(TestBrain.Create(), stub, posture, watch).StartAsync(ct);

    public static TestBrain Compose(TestBrain brain, GoogleStub stub, string? posture = null, bool watch = false)
    {
        var builder = brain
            .WithModule<GoogleAuthModule, GoogleAuthModuleOptions>()
            .WithModule<GmailModule, GmailModuleOptions>(options =>
            {
                options.ProfileEndpoint = stub.ProfileEndpoint;
                options.GmailEndpoint = stub.GmailEndpoint;
            })
            .WithIntegration(GoogleAuthModule.Integration, "ClientId", "integration-client")
            .WithIntegration(GoogleAuthModule.Integration, "ClientSecret", "integration-secret")
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
            var pubSub = GmailModule.Integration;
            builder = builder.WithIntegration(pubSub, "Audience", "https://push.example.test/gmail")
                .WithIntegration(pubSub, "ServiceAccountEmail", "push@example.test")
                .WithIntegration(pubSub, "TopicName", "projects/example/topics/gmail");
        }
        if (posture is not null) { builder = builder.WithConfiguration(IntoChat.OS.Connections.Identity.Configuration.AuthOptions.PostureKey, posture); }
        return builder;
    }

    // The Gmail connection is a plain neuron (06-08): its durable reminders live on its runtime host grain.
    public static Orleans.Runtime.GrainId GmailHost(string connectionKey)
        => Orleans.Runtime.GrainId.Create("neuron.host", IntoChat.OS.Connections.Platform.Connections.ConnectionGrains.IdFor(connectionKey).ToString());

    // Fires the Gmail lifecycle reminder as the reminder service would.
    public static Task RemindGmail(this ITestBrain brain, string connectionKey, CancellationToken cancellationToken = default)
        => IntoChat.OS.Kernel.Composition.KernelTestHooks.Remind(brain.Services, GmailHost(connectionKey), GmailNeuron.LifecycleReminder, cancellationToken);
    public static IntoChat.OS.Runtime.IPlainNeurons Plain(this ITestBrain brain) => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();

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
