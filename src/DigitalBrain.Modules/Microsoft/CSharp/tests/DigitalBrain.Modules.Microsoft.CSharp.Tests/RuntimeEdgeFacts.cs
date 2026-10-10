using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class RuntimeEdgeFacts
{
    [Fact]
    public async Task AcknowledgementReportsSupportBatchesAndRejectAmbiguousOrStaleRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);
        var runtime = brain.Grains.GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For("acc-brain"));
        var neuron = brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain"));
        var pinger = brain.Get<IPinger>("acknowledged");
        var subscription = await runtime.Subscribed(session.Id, "acc-brain/csharp/a", pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<DigitalBrain.Microsoft.CSharp.Runtime.Signals.BrainRuntimeChanged>(neuron, ct);
        await pinger.Ping(1); await changes.NextAsync(ct: ct);
        using var ambiguous = await Report(brain, session, new RuntimeReport(
            Acknowledge: new(subscription, 1), Acknowledgements: [new(subscription, 1)]), ct);
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        Assert.Single(await runtime.PendingBatches(session.Id));
        using var batch = await Report(brain, session, new RuntimeReport(Acknowledgements: [new(subscription, 1)]), ct);
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.Empty(await runtime.PendingBatches(session.Id));
        await changes.NextAsync(ct: ct);
        await pinger.Ping(2);
        await changes.NextAsync(ct: ct);
        using var legacy = await Report(brain, session, new RuntimeReport(Acknowledge: new(subscription, 2)), ct);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Empty(await runtime.PendingBatches(session.Id));
        await brain.DeactivateAsync(neuron, ct);
        Assert.NotEqual(session.Id, (await neuron.Inspect(ct)).SessionId);
        using var stale = await Report(brain, session, new RuntimeReport(Acknowledgements: [new(subscription, 3)]), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact]
    public async Task AnInvocationChecksTheSessionAndBehaviorInOneRuntimeRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = calls.Enqueue,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, traced: true);
        var session = await Assigned(brain, ct);
        var edge = brain.SiloServices.GetRequiredService<RuntimeEdge>();
        using var invocation = new System.Diagnostics.Activity("runtime invocation").Start();

        var result = await edge.InvokeAsync("acc-brain", session.Token,
            new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.Double), [JsonSerializer.SerializeToElement(21)]), ct);

        Assert.Equal(42, result!.Value.GetInt32());
        Assert.Single(calls, call => call.TraceId == invocation.TraceId
            && call.Kind == System.Diagnostics.ActivityKind.Client
            && call.OperationName is "IBrainRuntimeEdge/Authorize" or "IBrainRuntimeEdge/Identity");
    }

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
    public async Task AReplacedSessionCannotInvokeThroughAnUnchangedToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true, time: time);
        var stale = await Assigned(brain, ct);
        time.Now += TimeSpan.FromMinutes(3);
        await brain.Get<IBrainRuntime>(BrainRuntimeKeys.For("acc-brain")).Touch(ct);
        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.Double), [JsonSerializer.SerializeToElement(21)]);

        using var response = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post,
            "/brains/acc-brain/" + RuntimeProtocol.Invoke, stale.Token, invoke), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
    public async Task TheStreamSendsReadyAFreshTokenThenAssignmentsAndInvokeReachesANeuron()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);
        (await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct)).EnsureSuccessStatusCode();

        using var stream = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Get, "/brains/acc-brain/" + RuntimeProtocol.Stream, session.Token), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var events = SseParser.Create(await stream.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(ReadyEvent.Kind, events.Current.EventType);
        Assert.Equal(["acc-brain/csharp/a"], JsonSerializer.Deserialize<ReadyEvent>(events.Current.Data, RuntimeProtocol.Json)!.Behaviors);
        // Every stream hands the host a fresh token, so one that reconnects after a missed rotation keeps speaking.
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(TokenEvent.Kind, events.Current.EventType);
        var fresh = JsonSerializer.Deserialize<TokenEvent>(events.Current.Data, RuntimeProtocol.Json)!.Token;
        Assert.Equal(session.Id, brain.SiloServices.GetRequiredService<SessionTokens>().Validate(fresh)!.Session);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(AssignmentEvent.Kind, events.Current.EventType);
        Assert.Contains("acc-brain/csharp/a", events.Current.Data, StringComparison.Ordinal);

        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.Double), [JsonSerializer.SerializeToElement(21)]);
        using var invoked = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke, session.Token, invoke), ct);

        Assert.Equal(HttpStatusCode.OK, invoked.StatusCode);
        Assert.Equal(42, await invoked.Content.ReadFromJsonAsync<int>(ct));
    }

    [Fact]
    public async Task AKernelGrainIsNotACallableContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await Assigned(brain, ct);
        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).FullName!, "acc-brain", "List", []);

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

        Assert.Equal([ReadyEvent.Kind, TokenEvent.Kind, AssignmentEvent.Kind, SignalBatchEvent.Kind], await Kinds(events, 4));
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
    public async Task AKernelGrainIsRefusedEvenWithAForgedPlatformCaller()
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
            new RuntimeReport(Subscribe: new(Key("attacker"), typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).FullName!, "x", "Pinged"))), ct);
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

    [Fact]
    public async Task ABehaviorAsksTheKernelForItsOwnBrainsInstancesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, http: true);
        var session = await StartAsAlice(brain, Key("kernel"), ct);
        static Instance[] Listed(JsonElement json) => json.Deserialize<Instance[]>(RuntimeProtocol.Json)!;

        var files = Listed(await Invoke<JsonElement>(brain, session, Key("kernel"),
            new("", RuntimeProtocol.KernelContract, Alice.BrainId, RuntimeProtocol.KernelInstances, [Json(typeof(ICSharpFile).FullName), Json(null)]), ct));

        Assert.Contains(files, instance => instance.Key == Key("kernel"));
        Assert.All(files, instance => Assert.StartsWith(Alice.BrainId + "/", instance.Key, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("kernel"),
            new("", RuntimeProtocol.KernelContract, Alice.BrainId, "Retire", [Json("t"), Json("k")]), ct)).StatusCode);
    }

    private static JsonElement Json(string? value) => JsonSerializer.SerializeToElement(value, RuntimeProtocol.Json);

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

    private static string Fingerprint(ModuleBrain brain) => DigitalBrain.Client.Runtime.ContractsFingerprint.Of(brain.SiloServices.GetRequiredService<DigitalBrain.Framework.CompiledTypes>().Assemblies);

    private static async Task<(string Id, string Token)> Assigned(ModuleBrain brain, CancellationToken ct)
    {
        await brain.Grains.GetGrain<IBrainPrograms>(BrainRuntimeKeys.For("acc-brain")).Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
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
