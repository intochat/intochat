using System.Net;
using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class RevisionConflictEdgeFacts
{
    [Fact]
    public async Task AGuardedNeuronAuthorizationFailureKeepsItsTypeAcrossTheRuntimeEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        var invocation = new RuntimeInvocation("acc-brain/csharp/a", typeof(ICSharpFile).FullName!,
            "another-brain/csharp/private", nameof(ICSharpFile.Inspect), []);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke)
        { Content = JsonContent.Create(invocation, options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new("Bearer", session.Token);
        using var response = await brain.HttpClient.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains(RuntimeProtocol.RefusalHeader));
        var detail = await response.Content.ReadAsStringAsync(ct);

        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/acc-brain"),
            session.Token, RuntimeBrain.Fingerprint(brain), new Forward(brain.HttpClient), ct);
        var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connection.InvokeAsync(invocation, ct));
        Assert.Equal(detail, denied.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnclassifiedNonAuthorizationFailuresKeepTheirExistingType(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/acc-brain"),
            "token", "fingerprint", new Refusing("unrelated failure", status, null), ct);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.InvokeAsync(
            new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.RefuseStale), []), ct));
        Assert.Contains($"({(int)status}): unrelated failure", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleRevisionLeavesTheEdgeAs409WithTheCurrentRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);

        using var response = await brain.HttpClient.SendAsync(Invocation(session.Token), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RevisionConflictException.Code, Assert.Single(response.Headers.GetValues(RuntimeProtocol.RefusalHeader)));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(7, body.RootElement.GetProperty("currentRevision").GetInt64());
    }

    [Fact]
    public async Task TheScriptClientRaisesTheConflictItWasRefusedWith()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/acc-brain"), session.Token, RuntimeBrain.Fingerprint(brain), new Forward(brain.HttpClient), ct);

        var conflict = await Assert.ThrowsAsync<RevisionConflictException>(() => connection.InvokeAsync(
            new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.RefuseStale), []), ct));

        Assert.Equal(7, conflict.CurrentRevision);
    }

    [Theory]
    [InlineData("the proxy fell over")]
    [InlineData("{\"message\":\"stale\"}")]
    [InlineData("{\"message\":\"stale\",\"currentRevision\":\"seven\"}")]
    public async Task AConflictBodyOfAnotherShapeIsStillARefusal(string body)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/acc-brain"), "token", "fingerprint", new Refusing(body), ct);

        var refusal = await Assert.ThrowsAsync<ScriptRefusalException>(() => connection.InvokeAsync(
            new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.RefuseStale), []), ct));

        Assert.Equal(body, refusal.Message);
        Assert.Equal(RevisionConflictException.Code, refusal.Code);
    }

    private static HttpRequestMessage Invocation(string token)
    {
        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.RefuseStale), []);
        var request = new HttpRequestMessage(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke) { Content = JsonContent.Create(invoke, options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    private static async Task<(string Id, string Token)> Assigned(ITestBrain brain, CancellationToken ct)
    {
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var id = (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).SessionId!;
        return (id, brain.Services.GetRequiredService<SessionTokens>().Issue("acc-brain", id));
    }

    // The script client dials an absolute edge URL; the test brain is reached through its own client.
    private sealed class Forward(HttpClient brain) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery) { Content = request.Content };
            foreach (var header in request.Headers) { forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return await brain.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }

    // Accepts the connect report and the stream; refuses every invocation as a conflict with the given body.
    private sealed class Refusing(string body, HttpStatusCode status = HttpStatusCode.Conflict,
        string? code = RevisionConflictException.Code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(RuntimeProtocol.Invoke, StringComparison.Ordinal))
            {
                var refused = new HttpResponseMessage(status) { Content = new StringContent(body) };
                if (code is not null) { refused.Headers.Add(RuntimeProtocol.RefusalHeader, code); }
                return Task.FromResult(refused);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }
}
