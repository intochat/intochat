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
    public async Task AStampedBehaviorKeepsItsRecordedCrossBrainConnectionBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        var binding = new RecordedBinding();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .WithConfiguration("DigitalBrain:Auth:Posture", "Secured")
            .ConfigureServices(services => services.AddSingleton<IConnectionBindings>(binding)).StartAsync(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var behavior = Key(alice, "bound");
        var session = await StartAs(brain, alice, behavior, ct);
        var connection = "another-brain/connection/probe/shared";
        binding.App = behavior;
        binding.Connection = connection;

        using var allowed = await Send(brain, session, behavior,
            new("", typeof(IConnectionProbe).FullName!, connection, nameof(IConnectionProbe.Read), []), ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var subscribed = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post,
            "/brains/" + alice.BrainId + "/" + RuntimeProtocol.Report, session.Token,
            new RuntimeReport(Subscribe: new(behavior, typeof(IConnectionProbe).FullName!, connection, nameof(Pinged)))), ct);
        Assert.Equal(HttpStatusCode.OK, subscribed.StatusCode);
        using var refused = await Send(brain, session, behavior,
            new("", typeof(IConnectionProbe).FullName!, "another-brain/connection/probe/unbound", nameof(IConnectionProbe.Read), []), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    private sealed class RecordedBinding : IConnectionBindings
    {
        public string App { get; set; } = "";
        public string Connection { get; set; } = "";
        public ValueTask<bool> IsBound(CallerContext caller, string connectionKey, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(caller.Kind == CallerKind.App && caller.AppId == App && connectionKey == Connection);
    }

    [Fact]
    public async Task ASecuredHostAcceptsItsSessionTokenWithoutAUserCookieAndRejectsMissingOrInvalidTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .WithConfiguration("DigitalBrain:Auth:Posture", "Secured").StartAsync(ct);
        await brain.Get<IBrain>("acc-brain").Create(new Owner("acc"));
        await brain.Get<IBrain>("another-brain").Create(new Owner("another"));
        var session = await Assigned(brain, ct);

        using var connected = await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        using var invoked = await Send(brain, session, "acc-brain/csharp/a", Call(nameof(IPinger.Double), 21), ct);
        Assert.Equal(HttpStatusCode.OK, invoked.StatusCode);
        Assert.Equal(42, await invoked.Content.ReadFromJsonAsync<int>(ct));
        using var foreign = await Send(brain, session, "acc-brain/csharp/a",
            new("", typeof(ICSharpFile).FullName!, "another-brain/csharp/private", nameof(ICSharpFile.Inspect), []), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, foreign.StatusCode);
        using var subscription = await Report(brain, session,
            new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IPinger).FullName!, "another-brain/private", nameof(Pinged))), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, subscription.StatusCode);
        using var ownBrain = await Send(brain, session, "acc-brain/csharp/a",
            new("", typeof(IBrain).FullName!, "acc-brain", nameof(IBrain.Read), []), ct);
        Assert.Equal(HttpStatusCode.OK, ownBrain.StatusCode);
        using var otherBrain = await Send(brain, session, "acc-brain/csharp/a",
            new("", typeof(IBrain).FullName!, "another-brain", nameof(IBrain.Read), []), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, otherBrain.StatusCode);
        using var ownRootSubscription = await Report(brain, session,
            new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IBrain).FullName!, "acc-brain", nameof(Created))), ct);
        Assert.Equal(HttpStatusCode.OK, ownRootSubscription.StatusCode);
        using var otherRootSubscription = await Report(brain, session,
            new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IBrain).FullName!, "another-brain", nameof(Created))), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, otherRootSubscription.StatusCode);

        foreach (var (method, path) in new[] { (HttpMethod.Post, RuntimeProtocol.Invoke), (HttpMethod.Post, RuntimeProtocol.Report), (HttpMethod.Get, RuntimeProtocol.Stream) })
        {
            foreach (var token in new string?[] { null, "not.atoken" })
            {
                using var request = new HttpRequestMessage(method, "/brains/acc-brain/" + path)
                { Content = method == HttpMethod.Get ? null : JsonContent.Create(new { }) };
                if (token is not null) { request.Headers.Authorization = new("Bearer", token); }
                using var response = await brain.HttpClient.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task AcknowledgementReportsSupportBatchesAndRejectAmbiguousOrStaleRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        var runtime = RuntimeBrain.Edge(brain, "acc-brain");
        var neuron = RuntimeBrain.Runtime(brain, "acc-brain");
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
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        var edge = brain.Services.GetRequiredService<RuntimeEdge>();
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
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
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
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, time: time);
        var stale = await Assigned(brain, ct);
        time.Now += TimeSpan.FromMinutes(3);
        await RuntimeBrain.Runtime(brain, "acc-brain").Touch(ct);
        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(IPinger.Double), [JsonSerializer.SerializeToElement(21)]);

        using var response = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post,
            "/brains/acc-brain/" + RuntimeProtocol.Invoke, stale.Token, invoke), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenForAnotherBrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var token = brain.Services.GetRequiredService<SessionTokens>().Issue("other-brain", "s1");

        using var response = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Report, token, new RuntimeReport(Heartbeat: true)), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenOfARelaunchedSessionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, time: time);
        var stale = await Assigned(brain, ct);
        // Silent past the limit: the next touch replaces the session.
        time.Now += TimeSpan.FromMinutes(3);
        await RuntimeBrain.Runtime(brain, "acc-brain").Touch(ct);

        using var response = await Report(brain, stale, new RuntimeReport(Heartbeat: true), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AMismatchedContractsFingerprintIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);

        using var response = await Report(brain, session, new RuntimeReport(Fingerprint: "DigitalBrain=stale"), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SessionStatus.Refused, (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).Session);
    }

    [Fact]
    public async Task TheStreamSendsReadyAFreshTokenThenAssignmentsAndInvokeReachesANeuron()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
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
        Assert.Equal(session.Id, brain.Services.GetRequiredService<SessionTokens>().Validate(fresh)!.Session);
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
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).FullName!, "acc-brain", "List", []);

        using var invoked = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke, session.Token, invoke), ct);

        Assert.Equal(HttpStatusCode.BadRequest, invoked.StatusCode);
    }

    [Fact]
    public async Task ASubscribeRegistersBeforeTheFirstBatchAndAnArrivalIsPushed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
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
    [Fact]
    public async Task AnArrivalCarriesTheTraceThatPublishedIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
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
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        (await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct)).EnsureSuccessStatusCode();
        using var subscribed = await Report(brain, session, new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(Pinged))), ct);
        await using var changes = await brain.Observe<DigitalBrain.Microsoft.CSharp.Runtime.Signals.BrainRuntimeChanged>(RuntimeBrain.Runtime(brain, "acc-brain"), ct);
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
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        string Key(string name) => RuntimeEdgeFacts.Key(alice, name);
        var session = await StartAs(brain, alice, Key("report"), ct);

        Assert.Equal(42, await Invoke<int>(brain, session, Key("report"), Call(nameof(IPinger.Double), 21), ct));
        Assert.Equal(alice.PrincipalId + " App AppProxy " + Key("report"), await Invoke<string>(brain, session, Key("report"), Call(nameof(IPinger.Caller)), ct));
    }

    [Fact]
    public async Task ARetiredBehaviorCannotInvokeAndForgedCallsAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        string Key(string name) => RuntimeEdgeFacts.Key(alice, name);
        await StartAs(brain, alice, Key("stopped"), ct);
        var session = await StartAs(brain, alice, Key("other"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(brain, (session.Id, session.Token[..^2] + "AA"), Key("other"), Call(nameof(IPinger.Double), 1), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("other"), new("", typeof(ICSharpFile).FullName!, "pinger", "Double", [JsonSerializer.SerializeToElement(1)]), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("other"), Call("Watch", 1), ct)).StatusCode);
        using (alice.AsCaller()) { await brain.Get<ICSharpFile>(Key("stopped")).Stop(ct); }
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("stopped"), Call(nameof(IPinger.Double), 1), ct)).StatusCode);
    }

    [Fact]
    public async Task AKernelGrainIsRefusedEvenWithAForgedPlatformCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        string Key(string name) => RuntimeEdgeFacts.Key(alice, name);
        var session = await StartAs(brain, alice, Key("attacker"), ct);
        var forged = JsonSerializer.SerializeToElement(new { principalId = "x", accountId = "x", brainId = "x", kind = "Platform", stampedBy = "Platform", appId = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("attacker"),
            new("", typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!, "integration/openai", "Release", [forged]), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("attacker"),
            new("", typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).FullName!, "owner", "Resolve", [forged, forged]), ct)).StatusCode);
        using var subscribe = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/" + alice.BrainId + "/" + RuntimeProtocol.Report, session.Token,
            new RuntimeReport(Subscribe: new(Key("attacker"), typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).FullName!, "x", "Pinged"))), ct);
        Assert.Equal(HttpStatusCode.BadRequest, subscribe.StatusCode);
    }

    [Fact]
    public async Task ABehaviorCanInspectItsOwnFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        string Key(string name) => RuntimeEdgeFacts.Key(alice, name);
        var session = await StartAs(brain, alice, Key("inspect"), ct);

        var inspected = await Invoke<JsonElement>(brain, session, Key("inspect"), new("", typeof(ICSharpFile).FullName!, Key("inspect"), nameof(ICSharpFile.Inspect), []), ct);

        Assert.True(inspected.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task ABehaviorAsksTheKernelForItsOwnBrainsInstancesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        string Key(string name) => RuntimeEdgeFacts.Key(alice, name);
        var session = await StartAs(brain, alice, Key("kernel"), ct);
        static Instance[] Listed(JsonElement json) => json.Deserialize<Instance[]>(RuntimeProtocol.Json)!;

        var files = Listed(await Invoke<JsonElement>(brain, session, Key("kernel"),
            new("", RuntimeProtocol.KernelContract, alice.BrainId, RuntimeProtocol.KernelInstances, [Json(typeof(ICSharpFile).FullName), Json(null)]), ct));

        Assert.Contains(files, instance => instance.Key == Key("kernel"));
        Assert.All(files, instance => Assert.StartsWith(alice.BrainId + "/", instance.Key, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("kernel"),
            new("", RuntimeProtocol.KernelContract, alice.BrainId, "Retire", [Json("t"), Json("k")]), ct)).StatusCode);
    }

    private static JsonElement Json(string? value) => JsonSerializer.SerializeToElement(value, RuntimeProtocol.Json);

    private static string Key(Person alice, string name) => CSharpFileKeys.For(alice.BrainId, name);

    private static RuntimeInvocation Call(string method, params int[] arguments)
        => new("", typeof(IPinger).FullName!, "pinger", method, [.. arguments.Select(argument => JsonSerializer.SerializeToElement(argument))]);

    private static async Task<(string Id, string Token)> StartAs(ITestBrain brain, Person alice, string fileId, CancellationToken ct)
    {
        var file = brain.Get<ICSharpFile>(fileId);
        using (alice.AsCaller())
        {
            await file.Write("Console.WriteLine(1);", ct);
            await file.Start(ct);
        }
        var id = (await RuntimeBrain.Runtime(brain, alice.BrainId).Inspect(ct)).SessionId!;
        return (id, brain.Services.GetRequiredService<SessionTokens>().Issue(alice.BrainId, id));
    }

    private static Task<HttpResponseMessage> Send(ITestBrain brain, (string Id, string Token) session, string behavior, RuntimeInvocation invocation, CancellationToken ct)
        => brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/" + BrainKey.ScopeOf(behavior) + "/" + RuntimeProtocol.Invoke, session.Token, invocation with { Behavior = behavior }), ct);

    private static async Task<T> Invoke<T>(ITestBrain brain, (string Id, string Token) session, string behavior, RuntimeInvocation invocation, CancellationToken ct)
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

    private static string Fingerprint(ITestBrain brain) => RuntimeBrain.Fingerprint(brain);

    private static async Task<(string Id, string Token)> Assigned(ITestBrain brain, CancellationToken ct)
    {
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var id = (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).SessionId!;
        return (id, brain.Services.GetRequiredService<SessionTokens>().Issue("acc-brain", id));
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    private static Task<HttpResponseMessage> Report(ITestBrain brain, (string Id, string Token) session, RuntimeReport report, CancellationToken ct)
        => brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Report, session.Token, report), ct);
}
