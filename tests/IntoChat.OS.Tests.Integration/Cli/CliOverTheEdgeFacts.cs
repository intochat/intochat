using System.Net;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using IntoChat.OS.Cli;
using IntoChat.OS.Kernel.DevUI;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Kernel.Platform.Proto;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Tests.Integration.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Integration.Cli;

// Every command of the command-line host against a real in-process kernel: its own Kestrel edge on an ephemeral loopback port
// and its own silo. The CLI holds no cluster client; it reaches the kernel through the gRPC neuron channel as the app "cli" for
// the person its proof names, or through the HTTP edge as that person.
[Trait("Category", "AccessFindings")]
public sealed class CliOverTheEdgeFacts(CliOverTheEdgeFacts.Kernel kernel) : IClassFixture<CliOverTheEdgeFacts.Kernel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The kernel is the full OS composition on the local topology, as the Desktop and a local brain start it: no gateway port is
    // configured, so the silo opens no Orleans gateway listener, and the CLI still reaches everything through the edge.
    [Fact]
    public async Task TheKernel_OpensNoOrleansGateway()
    {
        Assert.Equal(TopologyContext.NoGateway, kernel.Harness.Services.GetRequiredService<IOptions<EndpointOptions>>().Value.GatewayPort);
        Assert.Null(kernel.Harness.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress);
        Assert.False(await Listens(kernel.Harness.GatewayPort), "Nothing listens on the port the harness reserved for a gateway.");
    }

    // A loopback connect to the port this harness reserved itself; nothing else on the machine is probed.
    internal static async Task<bool> Listens(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    [Fact]
    public async Task Runs_ListsTheRunsIndex()
    {
        var (code, _, error) = await Cli(kernel.Presence, "runs");

        Assert.True(code == 0, error);
        Assert.Equal("", error);
    }

    [Fact]
    public async Task Send_ReachesTheAppRun_AndReportsItsOutcome()
    {
        var (code, output, error) = await Cli(kernel.Presence, "send", "no-such-app", "go", "{}");

        // Not refused at the edge or the gate: the app run itself answers that no such app is installed.
        Assert.Equal("", error);
        Assert.StartsWith("Failed ", output, StringComparison.Ordinal);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Start_OfAnUnknownWorkflow_IsTheWorkflowRunsAnswer_NotARefusal()
    {
        var (code, _, error) = await Cli(kernel.Presence, "start", "no-such-workflow", "input");

        Assert.Equal(1, code);
        Assert.Contains("InvalidArgument: No workflow named 'no-such-workflow'", error, StringComparison.Ordinal);
    }

    // The whole confirmation round as a person drives it from a terminal: start a workflow (the app "cli"), see it wait in the
    // runs list, approve its plan (the person), and the run finishes with the approved branch.
    [Fact]
    public async Task Start_ThenRuns_ThenApprove_FinishesTheWorkflow()
    {
        // The runs index hears what happens after it first wakes (RunsNeuron): listing once wakes it.
        Assert.Equal(0, (await Cli(kernel.Presence, "runs")).Code);
        var (code, started, error) = await Cli(kernel.Presence, "start", ApprovalDemoWorkflow.Name, "hello");
        Assert.True(code == 0, error);
        var runId = RunIdOf(started);

        var plan = (await WhenRun(runId, run => run.PlanId is not null)).PlanId!;
        // The runs index follows the run's signals, so it may lag the run by a moment.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string runs;
        while (!(runs = (await Cli(kernel.Presence, "runs")).Output).Split('\n').Any(line => line.StartsWith("Waiting", StringComparison.Ordinal) && line.Contains(runId, StringComparison.Ordinal)))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"runs never listed {runId} as waiting:\n{runs}");
            await Task.Delay(100, Ct);
        }

        var (approved, answer, _) = await Cli(kernel.Presence, "approve", plan);
        Assert.True(approved == 0, answer);

        var done = await WhenRun(runId, run => run.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed);
        Assert.Equal(WorkflowRunStatus.Completed, done.Status);
        Assert.Contains("written", done.Output ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_StopsAWaitingWorkflow()
    {
        var (_, started, _) = await Cli(kernel.Presence, "start", ApprovalDemoWorkflow.Name, "hello");
        var runId = RunIdOf(started);
        await WhenRun(runId, run => run.PlanId is not null);

        var (code, output, error) = await Cli(kernel.Presence, "cancel", runId, "done here");

        Assert.True(code == 0, error);
        Assert.Equal($"Cancelled {runId}", output.TrimEnd());
    }

    private static string RunIdOf(string started) => Assert.Single(started.Split(' ', StringSplitOptions.RemoveEmptyEntries), part => part.StartsWith("workflows/", StringComparison.Ordinal));

    private async Task<WorkflowRunView> WhenRun(string runId, Func<WorkflowRunView, bool> condition)
    {
        using var asCli = new NeuronsEdge(kernel.Harness.BaseAddress, kernel.Harness.Scope, new CliProof(CliProofKind.Presence, kernel.Harness.PresenceToken));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var run = await asCli.Call(typeof(IWorkflowRun), $"{kernel.Harness.Scope}/{runId}", nameof(IWorkflowRun.Read),
                new EmptyArgs(), CliJsonContext.Default.EmptyArgs, CliJsonContext.Default.WorkflowRunView, Ct);
            if (run is not null && condition(run)) { return run; }
            Assert.True(DateTimeOffset.UtcNow < deadline, $"{runId} is still {run?.Status} after 30s.");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Cancel_OfARunThatIsNotCancellable_SaysSo_WithoutCallingTheKernel()
    {
        var (code, output, _) = await Cli(kernel.Presence, "cancel", "apps/whatever");

        Assert.Equal(1, code);
        Assert.StartsWith("No cancellable run apps/whatever", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    public async Task ApproveAndDeny_AnswerAsThePerson_OverTheHttpEdge(string verb)
    {
        var (_, output, error) = await Cli(kernel.Presence, verb, "no-such-plan");

        Assert.Equal("", error);
        // The edge took the person and handed the answer to the brain; an unknown plan is the brain's answer, not a refusal.
        Assert.DoesNotMatch("^40[13] ", output);
        Assert.EndsWith($"{(verb == "approve" ? "approved" : "denied")} no-such-plan", output.TrimEnd(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runs")]
    [InlineData("start", "no-such-workflow")]
    public async Task WithoutAProof_TheKernelAnswersUnauthenticated(params string[] args)
    {
        var (code, _, error) = await Cli(environment: null, args);

        Assert.Equal(1, code);
        Assert.Contains("Unauthenticated", error, StringComparison.Ordinal);
        Assert.Contains(CliProof.PresenceVariable, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAWrongPresenceToken_TheKernelAnswersUnauthenticated()
    {
        var (code, _, error) = await Cli(new() { [CliProof.PresenceVariable] = "not-the-token-not-the-token-not-the-token" }, "runs");

        Assert.Equal(1, code);
        Assert.Contains("Unauthenticated", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    public async Task ApproveAndDeny_WithoutAProof_AreUnauthorized(string verb)
    {
        var (code, output, _) = await Cli(environment: null, verb, "no-such-plan");

        Assert.Equal(1, code);
        Assert.StartsWith("401 ", output, StringComparison.Ordinal);
    }

    // The narrowing is real: as the app "cli" the owner reaches only what the brain grants that app, so the confirmation inbox,
    // which the same person reads as themself, is refused.
    [Fact]
    public async Task TheCli_IsTheCliApp_NotThePerson()
    {
        using var asCli = new NeuronsEdge(kernel.Harness.BaseAddress, kernel.Harness.Scope, new CliProof(CliProofKind.Presence, kernel.Harness.PresenceToken));
        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            asCli.Call(typeof(IConfirmationInbox), IConfirmationInbox.Key(kernel.Harness.Scope), nameof(IConfirmationInbox.Read),
                new EmptyArgs(), CliJsonContext.Default.EmptyArgs, CliJsonContext.Default.JsonElement, Ct));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);

        var asPerson = await Neurons(kernel, appHeader: null).CallAsync(ReadInbox(kernel.Harness.Scope), cancellationToken: Ct);
        Assert.False(asPerson.Result.IsEmpty);
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("guest")]
    [InlineData("kernel")]
    [InlineData("identity")]
    [InlineData("")]
    public async Task AnAppHeader_CanNameOnlyTheCliApp(string app)
    {
        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await Neurons(kernel, appHeader: app).CallAsync(ReadInbox(kernel.Harness.Scope), cancellationToken: Ct));

        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
    }

    [Fact]
    public async Task AnAppHeader_WithoutAProof_GrantsNothing()
    {
        var client = new Neurons.NeuronsClient(GrpcChannel.ForAddress(kernel.Harness.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler()),
            HttpVersion = HttpVersion.Version11,
        }));

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.CallAsync(ReadInbox(kernel.Harness.Scope), new Metadata { { ActingApp.HeaderName, "cli" } }, cancellationToken: Ct));

        Assert.Equal(StatusCode.Unauthenticated, refused.StatusCode);
    }

    private static CallRequest ReadInbox(string brain) => new()
    {
        BrainId = brain,
        Contract = typeof(IConfirmationInbox).FullName,
        Key = IConfirmationInbox.Key(brain),
        Method = nameof(IConfirmationInbox.Read),
        Arguments = ByteString.CopyFromUtf8("{}"),
    };

    private static Neurons.NeuronsClient Neurons(Kernel kernel, string? appHeader)
    {
        var headers = new Metadata { { PresenceProof.HeaderName, kernel.Harness.PresenceToken } };
        if (appHeader is not null) { headers.Add(ActingApp.HeaderName, appHeader); }
        var channel = GrpcChannel.ForAddress(kernel.Harness.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HeaderHandler(headers)),
            HttpVersion = HttpVersion.Version11,
        });
        return new Neurons.NeuronsClient(channel);
    }

    private sealed class HeaderHandler(Metadata headers) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var header in headers) { request.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private async Task<(int Code, string Output, string Error)> Cli(Dictionary<string, string>? environment, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var variables = new Dictionary<string, string>(environment ?? [], StringComparer.Ordinal)
        {
            ["INTOCHAT_BASE"] = kernel.Harness.BaseAddress.ToString(),
            ["INTOCHAT_BRAIN"] = kernel.Harness.Scope,
        };
        var code = await CliCommands.Run(args, output, error, name => variables.GetValueOrDefault(name));
        return (code, output.ToString(), error.ToString());
    }

    public sealed class Kernel : IAsyncLifetime
    {
        internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Harness { get; private set; } = null!;

        internal Dictionary<string, string> Presence => new() { [CliProof.PresenceVariable] = Harness.PresenceToken };

        // The approval demo workflow is DevUI's; the harness does not load DevUI, so the fixture registers the definition itself.
        public async ValueTask InitializeAsync() => Harness = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(CancellationToken.None,
            builder => builder.Services.AddSingleton(new WorkflowDefinition(ApprovalDemoWorkflow.Name, "Waits for a person.", ApprovalDemoWorkflow.Build)));

        public async ValueTask DisposeAsync()
        {
            if (Harness is not null) { await Harness.DisposeAsync(); }
        }
    }
}
