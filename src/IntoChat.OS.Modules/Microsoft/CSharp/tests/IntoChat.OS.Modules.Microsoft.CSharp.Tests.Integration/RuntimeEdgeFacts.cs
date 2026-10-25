using IntoChat.OS.Modules.Google.Gmail;
using System.Net;
using System.Net.ServerSentEvents;
using System.Text.Json;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.CSharp.Edge;
using IntoChat.OS.Microsoft.CSharp.Files;
using IntoChat.OS.Microsoft.CSharp.Runtime;
using IntoChat.OS.Microsoft.CSharp.Sessions;
using IntoChat.OS.Microsoft.CSharp.Catalog;
using IntoChat.OS.Postgres;
using IntoChat.OS.Google.Gmail;
using IntoChat.OS.Google.Gmail.Testing;
using IntoChat.OS.Framework.Platform.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class RuntimeEdgeFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task GmailTypedDiscoveryPagingDraftRecoveryAndCancellationCrossTheRealEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        google.AddMessage("m1", "First", "First body");
        google.AddMessage("m2", "Second", "Second body");
        google.MessageReplies["cancel-me"] = new(200, new { id = "cancel-me" }, Delay: TimeSpan.FromSeconds(30));
        google.DraftReplies.Enqueue(new(200, new { id = "draft-success" }));
        google.DraftReplies.Enqueue(new(200, Abort: true));
        await using var brain = await GmailBrain.Compose(RuntimeBrain.Create(new FakeSessionHost()), google).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "script-mail");
        using (brain.Owner.AsCaller())
        {
            await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1),
                GmailScopes.Read + " https://www.googleapis.com/auth/gmail.compose", null, "{}"));
            var discovery = brain.Services.GetRequiredService<CSharpContractDiscovery>();
            foreach (var method in new[] { nameof(IGmail.ReadInbox), nameof(IGmail.ReadMessage), nameof(IGmail.PreviewDraft), nameof(IGmail.CreateDraft), nameof(IGmail.GetDraftOperation) })
            {
                var detail = await discovery.ReadDetail("gmail", typeof(IGmail).FullName!, method, ct);
                Assert.False(detail.Truncated);
                var schema = Assert.Single(detail.Contracts);
                Assert.Contains("GmailFieldIssue(", schema, StringComparison.Ordinal);
                Assert.Contains(method + "(", schema, StringComparison.Ordinal);
            }
        }
        const string source = """
            using IntoChat.OS.Google.Gmail;
            await using var brain = await IntoChatOSClient.ConnectAsync(args);
            var gmail = brain.Get<IGmail>(brain.Setting("GmailConnection") ?? throw new InvalidOperationException("Select a Gmail connection."));
            var ct = brain.Stopping;
            var invalid = await gmail.ReadInbox(new GmailInboxRequest(0), ct);
            if (invalid.Failure?.Code != "invalid_input") throw new InvalidOperationException("Expected typed validation.");
            var first = (await gmail.ReadInbox(new GmailInboxRequest(1), ct)).Value ?? throw new InvalidOperationException("No first page.");
            var second = (await gmail.ReadInbox(new GmailInboxRequest(2, first.NextPageToken), ct)).Value ?? throw new InvalidOperationException("No continuation.");
            var message = (await gmail.ReadMessage(new GmailMessageRequest(first.Messages[0].MessageId), ct)).Value!;
            Console.WriteLine(first.Messages.Length + ":" + second.Messages.Length + ":" + message.TextBody);
            var preview = (await gmail.PreviewDraft(new GmailDraftRequest(["friend@example.test"], [], [], "Review", "Draft only."), ct)).Value!;
            var requestId = brain.Setting("DraftRequestId") ?? throw new InvalidOperationException("Retain a request ID.");
            var request = new GmailCreateDraftRequest(preview.PreviewId, requestId);
            var created = (await gmail.CreateDraft(request, ct)).Value!;
            var replay = (await gmail.CreateDraft(request, ct)).Value!;
            if (created.DraftId != replay.DraftId || replay.Status != GmailDraftOperationStatus.Completed) throw new InvalidOperationException("Replay changed the outcome.");
            Console.WriteLine("created:" + created.DraftId + ":" + created.Accounting?.Status);
            var uncertainPreview = (await gmail.PreviewDraft(new GmailDraftRequest(["friend@example.test"], [], [], "Review uncertain", "Do not retry automatically."), ct)).Value!;
            var uncertainId = requestId + "-uncertain";
            var uncertain = (await gmail.CreateDraft(new GmailCreateDraftRequest(uncertainPreview.PreviewId, uncertainId), ct)).Value!;
            var recovered = (await gmail.GetDraftOperation(new GmailDraftOperationRequest(uncertainId), ct)).Value!;
            if (uncertain.Status != GmailDraftOperationStatus.Unknown || recovered.Status != GmailDraftOperationStatus.Unknown) throw new InvalidOperationException("Expected retained uncertainty.");
            Console.WriteLine("unknown:retained");
            using var cancelled = new CancellationTokenSource();
            cancelled.CancelAfter(TimeSpan.FromSeconds(1));
            try { await gmail.ReadMessage(new GmailMessageRequest("cancel-me"), cancelled.Token); throw new InvalidOperationException("Cancellation was lost."); }
            catch (OperationCanceledException) { Console.WriteLine("cancelled"); }
            """;
        var compilation = brain.Services.GetRequiredService<CSharpScriptCheck>().Compile(new Dictionary<string, string> { ["gmail.cs"] = source });
        Assert.True(compilation.Success, string.Join("; ", compilation.Errors));
        var compiled = compilation.Assemblies["gmail.cs"];
        var behavior = Key(brain.Owner, "gmail-example");
        var session = await StartAs(brain, brain.Owner, behavior, ct);
        var blobs = new FakeBrainEdge();
        blobs.Serve(compiled);
        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/" + brain.Owner.BrainId),
            session.Token, Fingerprint(brain), new ForwardRuntime(brain.HttpClient), ct);
        await using var run = await BehaviorLoaderFacts.Loader(blobs).LoadAsync(connection,
            blobs.Assignment(behavior, compiled, new() { ["GmailConnection"] = key, ["DraftRequestId"] = "script-retained-request" }), NullLogger.Instance, ct);
        Assert.True(await run.Completion.WaitAsync(ct) == 0, run.Error + "\n" + run.Tail(30));
        Assert.Contains("1:1:First body", run.Tail(30), StringComparison.Ordinal);
        Assert.Contains("created:draft-success:", run.Tail(30), StringComparison.Ordinal);
        Assert.Contains("unknown:retained", run.Tail(30), StringComparison.Ordinal);
        Assert.Contains("cancelled", run.Tail(30), StringComparison.Ordinal);
        Assert.Equal(2, google.Requests.Count(call => call.Method == "POST" && call.Path.EndsWith("/drafts", StringComparison.Ordinal)));
        Assert.Single(google.Requests, call => call.Path.EndsWith("/messages/cancel-me", StringComparison.Ordinal));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task GmailReactiveScriptSubscribesThenReconcilesThroughTheRealEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        google.AddMessage("m1", "Current mailbox", "Current body");
        await using var brain = await GmailBrain.Compose(RuntimeBrain.Create(new FakeSessionHost()), google).StartAsync(ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "gmail", "reactive-mail");
        using (brain.Owner.AsCaller())
        { await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1), GmailScopes.Read, null, "{}")); }
        var compilation = brain.Services.GetRequiredService<CSharpScriptCheck>().Compile(new Dictionary<string, string>
        {
            ["gmail-reactive.cs"] = """
                using IntoChat.OS.Google.Gmail;
                await using var brain = await IntoChatOSClient.ConnectAsync(args);
                var gmail = brain.Get<IGmail>(brain.Setting("GmailConnection") ?? throw new InvalidOperationException("Select a Gmail connection."));
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
                async Task Reconcile()
                {
                    var inbox = await gmail.ReadInbox(new GmailInboxRequest(1), stop.Token);
                    Console.WriteLine("reconciled:" + inbox.Value!.Messages[0].Subject);
                    stop.Cancel();
                }
                try
                {
                    // The callback runs after subscription, closing the initial-read/subscription gap.
                    await foreach (var wakeup in brain.On<MailReceived>(gmail, Reconcile, stop.Token))
                        await Reconcile();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                """,
        });
        Assert.True(compilation.Success, string.Join("; ", compilation.Errors));
        var compiled = compilation.Assemblies["gmail-reactive.cs"];
        var behavior = Key(brain.Owner, "gmail-example");
        var session = await StartAs(brain, brain.Owner, behavior, ct);
        var blobs = new FakeBrainEdge();
        blobs.Serve(compiled);
        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/" + brain.Owner.BrainId),
            session.Token, Fingerprint(brain), new ForwardRuntime(brain.HttpClient), ct);
        await using var run = await BehaviorLoaderFacts.Loader(blobs).LoadAsync(connection,
            blobs.Assignment(behavior, compiled, new() { ["GmailConnection"] = key }), NullLogger.Instance, ct);
        Assert.True(await run.Completion.WaitAsync(ct) == 0, run.Error + "\n" + run.Tail(20));
        Assert.Contains("reconciled:Current mailbox", run.Tail(20), StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DiscoveredNestedRequestAndResultCompileAndCrossTheRealHttpEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new NestedQueryProvider();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost()).WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=sample;Username=reader")
            .ConfigureServices(services => services.AddSingleton<IPostgresProvider>(provider)).StartAsync(ct);
        var discovery = brain.Services.GetRequiredService<CSharpContractDiscovery>();
        using (brain.Owner.AsCaller())
        {
            var detail = await discovery.ReadDetail("postgres", typeof(IPostgresServer).FullName!, nameof(IPostgresServer.Query), ct);
            var schema = Assert.Single(detail.Contracts);
            Assert.Contains("PostgresParameter(", schema, StringComparison.Ordinal);
            Assert.Contains("PostgresQueryResult(", schema, StringComparison.Ordinal);
            Assert.Contains("PostgresColumn(", schema, StringComparison.Ordinal);
            Assert.False(detail.Truncated);
        }
        var compilation = brain.Services.GetRequiredService<CSharpScriptCheck>().Compile(new Dictionary<string, string>
        {
            ["query.cs"] = """
                using IntoChat.OS.Postgres;
                await using var brain = await IntoChatOSClient.ConnectAsync(args);
                var result = await brain.Get<IPostgresServer>("acc-brain/database").Query(
                    new PostgresQuery("select $1", 7, [new PostgresParameter("message", "typed request")]));
                Console.WriteLine(result.Columns[0].Name + ":" + result.Rows[0][0] + ":" + result.RowCount);
                """,
        });
        Assert.True(compilation.Success, string.Join("; ", compilation.Errors));
        var compiled = compilation.Assemblies["query.cs"];
        var session = await Assigned(brain, ct);
        // Only assembly downloads are faked. The compiled client invokes the host's real HTTP runtime edge.
        var blobs = new FakeBrainEdge();
        blobs.Serve(compiled);
        await using var connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/acc-brain"),
            session.Token, Fingerprint(brain), new ForwardRuntime(brain.HttpClient), ct);
        await using var run = await BehaviorLoaderFacts.Loader(blobs).LoadAsync(connection,
            blobs.Assignment("acc-brain/csharp/a", compiled), NullLogger.Instance, ct);
        Assert.True(await run.Completion.WaitAsync(ct) == 0, run.Error + "\n" + run.Tail(20));
        Assert.Contains("answer:42:1", run.Tail(10), StringComparison.Ordinal);
        var call = Assert.Single(provider.Calls);
        Assert.Equal(7, call.MaxRows);
        Assert.Equal(new PostgresParameter("message", "typed request"), Assert.Single(call.Parameters));
    }

    private sealed class NestedQueryProvider : IPostgresProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<(int MaxRows, PostgresParameter[] Parameters)> Calls { get; } = new();
        public Task<PostgresQueryResult> QueryAsync(string sql, int maxRows, IReadOnlyList<PostgresParameter> parameters, CancellationToken cancellationToken)
        {
            Calls.Enqueue((maxRows, [.. parameters]));
            return Task.FromResult(new PostgresQueryResult([new("answer", "int4", "number")], [["42"]], 1, false, 0));
        }
        public Task<PostgresSchema> ReadSchemaAsync(string? table, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PostgresConnection> PingAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ForwardRuntime(HttpClient brain) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery) { Content = request.Content };
            foreach (var header in request.Headers) { forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return brain.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReportChecksTheSessionInItsMutationTurnWithoutAPreflightCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new RuntimeCalls();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .ConfigureServices(services => services.AddSingleton<IIncomingGrainCallFilter>(calls)).StartAsync(ct);
        var session = await Assigned(brain, ct);
        calls.Methods.Clear();

        using var response = await Report(brain, session, new RuntimeReport(Heartbeat: true), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([nameof(IBrainRuntimeEdge.Heartbeat)], calls.Methods.ToArray());
    }

    private sealed class RuntimeCalls : IIncomingGrainCallFilter
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Methods { get; } = new();
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.InterfaceMethod.DeclaringType == typeof(IBrainRuntimeEdge))
            { Methods.Enqueue(context.InterfaceMethod.Name); }
            await context.Invoke();
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStampedBehaviorKeepsItsRecordedCrossBrainConnectionBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        var binding = new RecordedBinding();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .WithConfiguration(IntoChat.OS.Connections.Identity.Configuration.AuthOptions.PostureKey, "Secured")
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

    [Trait("host", "own")]
    [Fact]
    public async Task ASecuredHostAcceptsItsSessionTokenWithoutAUserCookieAndRejectsMissingOrInvalidTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .WithConfiguration(IntoChat.OS.Connections.Identity.Configuration.AuthOptions.PostureKey, "Secured").StartAsync(ct);
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

    [Trait("host", "own")]
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
        await using var changes = await brain.Observe<IntoChat.OS.Microsoft.CSharp.Runtime.Signals.BrainRuntimeChanged>(neuron, ct);
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

    [Trait("host", "own")]
    [Fact]
    public async Task AnInvocationReadsIdentityFromTheRuntimeDirectory()
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
        Assert.DoesNotContain(calls, call => call.TraceId == invocation.TraceId
            && call.Kind == System.Diagnostics.ActivityKind.Client
            && call.OperationName is "IBrainRuntimeEdge/Authorize" or "IBrainRuntimeEdge/Identity");
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task ATokenForAnotherBrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var token = brain.Services.GetRequiredService<SessionTokens>().Issue("other-brain", "s1");

        using var response = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Report, token, new RuntimeReport(Heartbeat: true)), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task AMismatchedContractsFingerprintIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);

        using var response = await Report(brain, session, new RuntimeReport(Fingerprint: "IntoChat.OS=stale"), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SessionStatus.Refused, (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).Session);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
    [Fact]
    public async Task AKernelGrainIsNotACallableContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        var invoke = new RuntimeInvocation("acc-brain/csharp/a", typeof(IntoChat.OS.Framework.Platform.Connections.IConnectionIndex).FullName!, "acc-brain", "List", []);

        using var invoked = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke, session.Token, invoke), ct);

        Assert.Equal(HttpStatusCode.BadRequest, invoked.StatusCode);
    }

    [Trait("host", "own")]
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
    [Trait("host", "own")]
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

        // The trusted edge begins the publish's trace under the click's activity, as a stamped request does; the
        // signal journals it, and the batch derives from it whichever turn reads the signal.
        using var click = new System.Diagnostics.Activity("click").Start();
        using (IntoChat.OS.Framework.Compute.TraceContext.Enter(IntoChat.OS.Framework.Compute.TraceContext.Begin("acc", "acc-brain", new NeuronId("acc-brain/edge"))))
        { await brain.Get<IPinger>("pinger").Ping(9); }

        SseItem<string> batch;
        do { Assert.True(await events.MoveNextAsync()); batch = events.Current; } while (batch.EventType != SignalBatchEvent.Kind);
        var signals = JsonSerializer.Deserialize<SignalBatchEvent>(batch.Data, RuntimeProtocol.Json)!;
        Assert.Equal(click.TraceId.ToString(), signals.TraceParent!.Split('-')[1]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASecondStreamResendsAnUnacknowledgedBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var session = await Assigned(brain, ct);
        (await Report(brain, session, new RuntimeReport(Fingerprint: Fingerprint(brain)), ct)).EnsureSuccessStatusCode();
        using var subscribed = await Report(brain, session, new RuntimeReport(Subscribe: new("acc-brain/csharp/a", typeof(IPinger).FullName!, "pinger", nameof(Pinged))), ct);
        await using var changes = await brain.Observe<IntoChat.OS.Microsoft.CSharp.Runtime.Signals.BrainRuntimeChanged>(RuntimeBrain.Runtime(brain, "acc-brain"), ct);
        await brain.Get<IPinger>("pinger").Ping(3);
        await changes.NextAsync(ct: ct);

        using var stream = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Get, "/brains/acc-brain/" + RuntimeProtocol.Stream, session.Token), HttpCompletionOption.ResponseHeadersRead, ct);
        await using var events = SseParser.Create(await stream.Content.ReadAsStreamAsync(ct)).EnumerateAsync(ct).GetAsyncEnumerator(ct);

        Assert.Equal([ReadyEvent.Kind, TokenEvent.Kind, AssignmentEvent.Kind, SignalBatchEvent.Kind], await Kinds(events, 4));
    }

    [Trait("host", "own")]
    [Fact]
    // Phase F: the app proxy stamps the app's own principal, not the person's; the person stays as the one it acts for.
    public async Task ABehaviorCallsNeuronsAsItsAppPrincipalOnBehalfOfItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        string Key(string name) => RuntimeEdgeFacts.Key(alice, name);
        var session = await StartAs(brain, alice, Key("report"), ct);

        Assert.Equal(42, await Invoke<int>(brain, session, Key("report"), Call(nameof(IPinger.Double), 21), ct));
        Assert.Equal($"{AppPrincipal.Id(Key("report"))} App AppProxy {Key("report")} for {alice.PrincipalId}", await Invoke<string>(brain, session, Key("report"), Call(nameof(IPinger.Caller)), ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARetiredBehaviorCannotInvokeAndForgedCallsAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var alice = await brain.PersonAsync("alice", ct);
        string Key(string name) => RuntimeEdgeFacts.Key(alice, name);
        await StartAs(brain, alice, Key("stopped"), ct);
        var session = await StartAs(brain, alice, Key("other"), ct);

        // Flip a signature character that carries six significant bits: rewriting the last characters can leave the decoded
        // signature unchanged (the final base64url character holds unused bits), which would make the "forgery" the real token.
        var at = session.Token.Length - 10;
        var forged = session.Token[..at] + (session.Token[at] == 'A' ? 'B' : 'A') + session.Token[(at + 1)..];
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(brain, (session.Id, forged), Key("other"), Call(nameof(IPinger.Double), 1), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("other"), new("", typeof(ICSharpFile).FullName!, "pinger", "Double", [JsonSerializer.SerializeToElement(1)]), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("other"), Call("Watch", 1), ct)).StatusCode);
        using (alice.AsCaller()) { await brain.Get<ICSharpFile>(Key("stopped")).Stop(ct); }
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("stopped"), Call(nameof(IPinger.Double), 1), ct)).StatusCode);
    }

    [Trait("host", "own")]
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
            new("", typeof(IntoChat.OS.Framework.Platform.Integrations.IIntegration).FullName!, "integration/openai", "Release", [forged]), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(brain, session, Key("attacker"),
            new("", typeof(IntoChat.OS.Framework.Platform.Secrets.ISecrets).FullName!, "owner", "Resolve", [forged, forged]), ct)).StatusCode);
        using var subscribe = await brain.HttpClient.SendAsync(Authorized(HttpMethod.Post, "/brains/" + alice.BrainId + "/" + RuntimeProtocol.Report, session.Token,
            new RuntimeReport(Subscribe: new(Key("attacker"), typeof(IntoChat.OS.Framework.Platform.Connections.IConnectionIndex).FullName!, "x", "Pinged"))), ct);
        Assert.Equal(HttpStatusCode.BadRequest, subscribe.StatusCode);
    }

    [Trait("host", "own")]
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

    [Trait("host", "own")]
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
