using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Google.Gmail.Testing;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

public sealed class GmailProviderFacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Trait("host", "own")]
    [Fact]
    public async Task EachRetriedReadHasOneRealAuthorityRowUnderTheSelectedConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.StartAsync(google, ct);
        google.ListReplies.Enqueue(new(503, new { }));
        google.ListReplies.Enqueue(new(429, new { }, Headers: new Dictionary<string, string> { ["Retry-After"] = "1" }));
        google.ListReplies.Enqueue(new(200, new { messages = new[] { new { id = "message", threadId = "thread" } } }));
        using var caller = brain.Owner.AsCaller();
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "attempts");
        var invocation = Guid.NewGuid().ToString("N");
        var trace = TraceContext.Current!.TraceId;
        var result = await brain.Services.GetRequiredService<IGmailApi>().ListMessagesAsync("selected-token",
            new(Funding.OwnConnection, new(Neuron: key), invocation), 1, null, ct);
        Assert.Equal("message", Assert.Single(result.Value!.Messages).Id);
        Assert.Equal(3, google.Requests.Length);
        var page = (await brain.Owner.Http.GetFromJsonAsync<ComputeTracePage>($"/brains/{brain.Owner.BrainId}/compute/traces/{trace}", Json, ct))!;
        Assert.Equal(3, page.Rows.Length);
        Assert.Equal(3, page.Rows.Select(row => row.CostId).Distinct().Count());
        Assert.Equal(3, page.UnpricedCount);
        Assert.All(page.Rows, row =>
        {
            Assert.Equal(GoogleOperations.ListMessages, row.Item);
            Assert.Equal(CostKind.ExternalProvider, row.Kind);
            Assert.Equal(key, row.Neuron);
            Assert.Equal(invocation, row.InvocationId);
            Assert.Equal(FundingKind.OwnConnection, row.FundingKind);
            Assert.Equal(1, row.Quantity);
            Assert.Equal("request", row.Unit);
            Assert.Null(row.ListPriceMicros);
            Assert.Equal(0, row.Micros);
        });
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheOAuthCallbackMetersExchangeAndProfileWithItsStoredOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        var traces = new List<string>();
        await using var brain = await GmailBrain.Compose(TestBrain.Create(), google)
            .ConfigureServices(services => services.AddHttpClient("IntoChat.OS.Connections.OAuth.Metered")
                .AddHttpMessageHandler(() => new TraceCapture(traces))).StartAsync(ct);
        using var start = await brain.Owner.Http.PostAsJsonAsync($"/brains/{brain.Owner.BrainId}/connections/oauth-account/login",
            new { connectorId = "gmail", authMethodId = "google-oauth" }, ct);
        start.EnsureSuccessStatusCode();
        var url = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"];
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/connections/oauth/gmail/callback?code=code&state={state}");
        using var callback = await brain.Owner.Http.SendAsync(request, ct);
        callback.EnsureSuccessStatusCode();
        var trace = Assert.Single(traces);
        var page = (await brain.Owner.Http.GetFromJsonAsync<ComputeTracePage>($"/brains/{brain.Owner.BrainId}/compute/traces/{trace}", Json, ct))!;
        Assert.Equal(2, page.Rows.Length);
        Assert.Contains(page.Rows, row => row.Item == IntoChat.OS.Modules.Google.Auth.GoogleAuthOperations.CodeExchange);
        Assert.Contains(page.Rows, row => row.Item == GoogleOperations.Profile);
        Assert.All(page.Rows, row =>
        {
            Assert.Equal(ConnectionKeys.For(brain.Owner.BrainId, "gmail", "oauth-account"), row.Neuron);
            Assert.Equal(FundingKind.OwnConnection, row.FundingKind);
            Assert.Equal(PricingStatus.Unpriced, row.PricingStatus);
            Assert.Null(row.ListPriceMicros);
            Assert.Equal(0, row.Micros);
        });
    }

    private sealed class TraceCapture(List<string> traces) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            traces.Add(TraceContext.Current!.TraceId);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
