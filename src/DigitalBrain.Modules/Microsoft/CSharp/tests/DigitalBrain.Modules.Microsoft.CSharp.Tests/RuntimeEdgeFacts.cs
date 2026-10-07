using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class RuntimeEdgeFacts
{
    [Fact]
    public async Task AnExpiredTokenIsRefusedOnEveryRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        foreach (var (method, path) in new[] { (HttpMethod.Post, RuntimeProtocol.Invoke), (HttpMethod.Post, RuntimeProtocol.Report), (HttpMethod.Get, RuntimeProtocol.Stream) })
        {
            using var request = new HttpRequestMessage(method, "/brains/acc-brain/" + path) { Content = method == HttpMethod.Get ? null : JsonContent.Create(new { }) };
            request.Headers.Authorization = new("Bearer", "not.atoken");
            using var response = await brain.HttpClient.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task ATokenForAnotherBrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var token = brain.SiloServices.GetRequiredService<SessionTokens>().Issue("other-brain", "s1");

        using var response = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Report, token, new RuntimeReport(Heartbeat: true)), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenOfARelaunchedSessionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, http: true, time: time);
        var stale = await Assigned(brain, ct);
        // Silent past the limit: the next touch replaces the session.
        time.Now += TimeSpan.FromMinutes(3);
        await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")).Touch(ct);

        using var response = await Report(brain, stale, new RuntimeReport(Heartbeat: true), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AMismatchedContractsFingerprintIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);

        using var response = await Report(brain, session, new RuntimeReport(Fingerprint: "DigitalBrain=stale"), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SessionStatus.Refused, (await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")).Inspect(ct)).Session);
    }

    [Fact]
    public async Task TheStreamSendsReadyThenAssignmentsAndInvokeReachesANeuron()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);
        (await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct)).EnsureSuccessStatusCode();

        using var stream = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Get, "/brains/acc-brain/" + RuntimeProtocol.Stream, session.Token), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var events = SseParser.Create(await stream.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(ReadyEvent.Kind, events.Current.EventType);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(AssignmentEvent.Kind, events.Current.EventType);
        Assert.Contains("acc-brain/csharp/a", events.Current.Data, StringComparison.Ordinal);

        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.Double), [JsonSerializer.SerializeToElement(21)]);
        using var invoked = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke, session.Token, invoke), ct);

        Assert.Equal(HttpStatusCode.OK, invoked.StatusCode);
        Assert.Equal(42, await invoked.Content.ReadFromJsonAsync<int>(ct));
    }

    [Fact]
    public async Task APlatformOnlyContractIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);
        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(IPlatformOnlyPinger).FullName!, "pinger", nameof(IPlatformOnlyPinger.Ping), [JsonSerializer.SerializeToElement(1)]);

        using var invoked = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke, session.Token, invoke), ct);

        Assert.Equal(HttpStatusCode.BadRequest, invoked.StatusCode);
    }

    [Fact]
    public async Task ASubscribeRegistersBeforeTheFirstBatchAndAnArrivalIsPushed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);
        (await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct)).EnsureSuccessStatusCode();
        using var stream = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Get, "/brains/acc-brain/" + RuntimeProtocol.Stream, session.Token), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var events = SseParser.Create(await stream.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct).GetAsyncEnumerator(ct);

        using var subscribed = await Report(brain, session, new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(Pinged))), ct);
        var subscription = (await subscribed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("subscription").GetString();
        await brain.Get<IPinger>("pinger").Ping(7);

        SseItem<string> batch;
        do { Assert.True(await events.MoveNextAsync()); batch = events.Current; } while (batch.EventType != SignalBatchEvent.Kind);
        var signals = JsonSerializer.Deserialize<SignalBatchEvent>(batch.Data, RuntimeProtocol.Json)!;
        Assert.Equal((subscription, 1L), (signals.Subscription, signals.Watermark));
        Assert.Equal(7, SignalJson.Number(Assert.Single(signals.Signals)));
    }

    // The publish's trace rides the batch down the stream, so a behavior's reaction joins it.
    [Fact(Timeout = 60_000)]
    public async Task AnArrivalCarriesTheTraceThatPublishedIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true, traced: true);
        var session = await Assigned(brain, ct);
        (await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct)).EnsureSuccessStatusCode();
        using var stream = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Get, "/brains/acc-brain/" + RuntimeProtocol.Stream, session.Token), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var events = SseParser.Create(await stream.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct).GetAsyncEnumerator(ct);
        (await Report(brain, session, new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(Pinged))), ct)).EnsureSuccessStatusCode();

        using var click = new System.Diagnostics.Activity("click").Start();
        await brain.Get<IPinger>("pinger").Ping(9);

        SseItem<string> batch;
        do { Assert.True(await events.MoveNextAsync()); batch = events.Current; } while (batch.EventType != SignalBatchEvent.Kind);
        var signals = JsonSerializer.Deserialize<SignalBatchEvent>(batch.Data, RuntimeProtocol.Json)!;
        Assert.Equal(click.TraceId.ToString(), signals.TraceParent!.Split('-')[1]);
    }

    [Fact]
    public async Task ASecondStreamResendsAnUnacknowledgedBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);
        (await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct)).EnsureSuccessStatusCode();
        using var subscribed = await Report(brain, session, new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(Pinged))), ct);
        await using var changes = await brain.Observe<DigitalBrain.Microsoft.CSharp.Runtime.Signals.BrainRuntimeChanged>(brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")), ct);
        await brain.Get<IPinger>("pinger").Ping(3);
        await changes.NextAsync(ct: ct);

        using var stream = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Get, "/brains/acc-brain/" + RuntimeProtocol.Stream, session.Token), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var events = SseParser.Create(await stream.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct).GetAsyncEnumerator(ct);

        Assert.Equal([ReadyEvent.Kind, AssignmentEvent.Kind, SignalBatchEvent.Kind], await Kinds(events, 3));
    }

    [Fact]
    public async Task ABehaviorCallsNeuronsAsItsOwnerStampedAsAnApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await StartAsAlice(brain, Key("report"), ct);

        Assert.Equal(42, await Invoke<int>(brain, session, Key("report"), Call(nameof(IPinger.Double), 21), ct));
        Assert.Equal("alice App AppProxy " + Key("report"), await Invoke<string>(brain, session, Key("report"), Call(nameof(IPinger.Caller)), ct));
    }

    [Fact]
    public async Task BehaviorsOfOneAppShareItsVerifiedIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        CallerContextStamper.Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        var app = brain.Get<IAppBindingTestHost>("stable-app");
        await app.Bind(Key("behavior-one"));
        await app.Bind(Key("behavior-two"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.Bind(Key("behavior-three"), "someone-else"));
        await app.Start(Key("behavior-one"));
        await app.Start(Key("behavior-two"));
        Orleans.Runtime.RequestContext.Clear();
        var session = Session(brain, (await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Alice.BrainId)).Inspect(ct)).SessionId!);

        foreach (var behavior in new[] { Key("behavior-one"), Key("behavior-two") })
        { Assert.Equal("alice App AppProxy stable-app", await Invoke<string>(brain, session, behavior, Call(nameof(IPinger.Caller)), ct)); }
    }

    [Fact]
    public async Task ARetiredBehaviorCannotInvokeAndForgedCallsAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        await StartAsAlice(brain, Key("stopped"), ct);
        var session = await StartAsAlice(brain, Key("other"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(brain, (session.Id, session.Token[..^2] + "AA"), Key("other"), Call(nameof(IPinger.Double), 1), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("other"), new("", typeof(ICSharpFile).FullName!, "pinger", "Double", [JsonSerializer.SerializeToElement(1)]), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("other"), Call("Watch", 1), ct)).StatusCode);
        await brain.Get<ICSharpFile>(Key("stopped")).Stop(ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("stopped"), Call(nameof(IPinger.Double), 1), ct)).StatusCode);
    }

    [Fact]
    public async Task APlatformOnlyContractIsRefusedEvenWithAForgedPlatformCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await StartAsAlice(brain, Key("attacker"), ct);
        var forged = JsonSerializer.SerializeToElement(new { principalId = "x", accountId = "x", brainId = "x", kind = "Platform", stampedBy = "Platform", appId = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("attacker"),
            new("", typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!, "integration/openai", "Release", [forged]), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("attacker"),
            new("", typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).FullName!, "owner", "Resolve", [forged, forged]), ct)).StatusCode);
        using var subscribe = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/" + Alice.BrainId + "/" + RuntimeProtocol.Report, session.Token,
            new RuntimeReport(Subscribe: new(Key("attacker"), typeof(IPlatformOnlyPinger).FullName!, "x", "Pinged"))), ct);
        Assert.Equal(HttpStatusCode.BadRequest, subscribe.StatusCode);
    }

    [Fact]
    public async Task ABehaviorCanInspectItsOwnFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await StartAsAlice(brain, Key("inspect"), ct);

        var inspected = await Invoke<JsonElement>(brain, session, Key("inspect"), new("", typeof(ICSharpFile).FullName!, Key("inspect"), nameof(ICSharpFile.Inspect), []), ct);

        Assert.True(inspected.TryGetProperty("status", out _));
    }

    private static readonly CallerContext Alice = new()
    {
        PrincipalId = "alice",
        AccountId = "account-a",
        BrainId = "workspace-a",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    };

    private static string Key(string name) => CSharpFileKeys.For(Alice.BrainId, name);

    private static RuntimeInvocation Call(string method, params int[] arguments)
        => new("", typeof(IPinger).FullName!, "pinger", method, [.. arguments.Select(argument => JsonSerializer.SerializeToElement(argument))]);

    private static async Task<(string Id, string Token)> StartAsAlice(ModuleBrain brain, string fileId, CancellationToken ct)
    {
        var file = brain.Get<ICSharpFile>(fileId);
        CallerContextStamper.Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        await file.Write("Console.WriteLine(1);", ct);
        try { await file.Start(ct); }
        finally { Orleans.Runtime.RequestContext.Clear(); }
        return Session(brain, (await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Alice.BrainId)).Inspect(ct)).SessionId!);
    }

    private static (string Id, string Token) Session(ModuleBrain brain, string id)
        => (id, brain.SiloServices.GetRequiredService<SessionTokens>().Issue(Alice.BrainId, id));

    private static Task<HttpResponseMessage> Send(ModuleBrain brain, (string Id, string Token) session, string behavior, RuntimeInvocation invocation, CancellationToken ct)
        => brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/" + Alice.BrainId + "/" + RuntimeProtocol.Invoke, session.Token, invocation with { Behavior = behavior }), ct);

    private static async Task<T> Invoke<T>(ModuleBrain brain, (string Id, string Token) session, string behavior, RuntimeInvocation invocation, CancellationToken ct)
    {
        using var response = await Send(brain, session, behavior, invocation, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(RuntimeProtocol.Json, ct))!;
    }

    private static async Task<string[]> Kinds(IAsyncEnumerator<SseItem<string>> events, int count)
    {
        var kinds = new List<string>();
        for (var i = 0; i < count; i++) { Assert.True(await events.MoveNextAsync()); kinds.Add(events.Current.EventType); }
        return [.. kinds];
    }

    private static string Fingerprint(ModuleBrain brain) => brain.SiloServices.GetRequiredService<ScriptContracts>().Fingerprint;

    private static async Task<(string Id, string Token)> Assigned(ModuleBrain brain, CancellationToken ct)
    {
        await brain.Grains().GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For("acc-brain")).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var id = (await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")).Inspect(ct)).SessionId!;
        return (id, brain.SiloServices.GetRequiredService<SessionTokens>().Issue("acc-brain", id));
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    private static Task<HttpResponseMessage> Report(ModuleBrain brain, (string Id, string Token) session, RuntimeReport report, CancellationToken ct)
        => brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Report, session.Token, report), ct);
}
