using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Tests.Unit.Desktop;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Tests.Unit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// POST /act is the one route a person's action takes: a synapse unit and a sandboxed app's view neuron take commands through
// IScreen.Dispatch as a compiled screen and an agent app do (ActEndpointFacts, AgentAppJourneyFacts). There is no event route.
[Collection(nameof(RecordingApp))]
public sealed class ActReachesEveryHostFacts
{
    private const string Scope = "owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly NeuronId TasksView = new(new BrainId(Scope), UIVocabulary.ViewType, "apps/tasks/view");

    // The person the HTTP edge stamps for a signed-in caller.
    private static IDisposable AsPerson() => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("person-ann", "person-ann", new BrainId(Scope)));

    private static IDisposable AsOwner() => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId(Scope)));

    [Fact]
    public void TheAppUiRoutesHaveNoEventRoute()
    {
        var routes = RouteTable.Of(AppUiEndpoints.Map, services => services.Known<INeuronProxies>());

        Assert.Equal(
            ["GET /brains/{brainId}/apps/node", "GET /brains/{brainId}/apps/{name}/children/{**path}", "POST /brains/{brainId}/act", "POST /brains/{brainId}/apps/connect"],
            routes);
    }

    // ---- a synapse unit ----

    private static async Task<TestOS> UnitOnTestOS(string unit = "ctr")
    {
        var os = TestOS.Local().Register(typeof(CounterUnit).Assembly);
        os.Confirmations.ApproveAll();
        await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).Install(new DigitalBrain.Units.InstallRequest(unit, new Dictionary<string, string>()), Ct);
        await os.Idle();
        await os.Get<IKicker>(NeuronId.Parse("owner/kicker/a")).Kick();
        await os.Idle();
        return os;
    }

    private static Task<JsonElement> Counter(TestOS os) => os.Get<IStateReader>(NeuronId.Parse("owner/ctr/default")).Read("ctr");

    [Theory]
    [InlineData("owner/ctr")]
    [InlineData("owner/units/ctr")]
    public async Task AUnitTakesACommandWithTheFieldValueAndThePersonWhoActed(string neuron)
    {
        using var owner = AsOwner();
        await using var os = await UnitOnTestOS();

        IResult result;
        using (AsPerson()) { result = await AppUiEndpoints.Act(new(neuron, ".", "ctr.bump", null, "hi"), new TestOSBrain(os), Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current); }
        await os.Idle();

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var state = await Counter(os);
        Assert.Equal(1, state.GetProperty("bumps").GetInt32());
        Assert.Equal("hi", state.GetProperty("value").GetString());
        Assert.Equal("person-ann", state.GetProperty("by").GetString());
        Assert.Empty(os.Failures);
    }

    // The resolver names the unit by its address, and by an app name no app claims when the unit of that alias has a view.
    [Theory]
    [InlineData("owner/ctr")]
    [InlineData("owner/apps/ctr")]
    public async Task TheResolverRoutesAUnitAddressAndAnUnclaimedAppNameToTheUnit(string neuron)
    {
        using var owner = AsOwner();
        await using var os = await UnitOnTestOS();
        var brain = new TestOSBrain(os);

        IResult result;
        using (AsPerson()) { result = await AppUiEndpoints.Act(new(neuron, ".", "ctr.bump"), brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current, resolver: new ScreenTargetResolver(brain, [])); }
        await os.Idle();

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(1, (await Counter(os)).GetProperty("bumps").GetInt32());
    }

    // Only the kernel names who acted: args that name "by" are refused before anything is journaled.
    [Fact]
    public async Task AUnitCommandWhoseArgsNameByIsRefused()
    {
        using var owner = AsOwner();
        await using var os = await UnitOnTestOS();

        IResult result;
        using (AsPerson())
        { result = await AppUiEndpoints.Act(new("owner/ctr", ".", "ctr.bump", JsonSerializer.SerializeToElement(new { by = "mallory" })), new TestOSBrain(os), Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current); }
        await os.Idle();

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, (await Counter(os)).GetProperty("bumps").GetInt32());
    }

    [Fact]
    public async Task AUnitCommandWithoutAPersonIsRefused()
    {
        using var owner = AsOwner();
        await using var os = await UnitOnTestOS();

        // The edge stamped no person for this request: the owner above only installed the unit.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Act(new("owner/ctr", ".", "ctr.bump"), new TestOSBrain(os), Scope, Ct, caller: null));
        await os.Idle();

        Assert.Equal(0, (await Counter(os)).GetProperty("bumps").GetInt32());
    }

    // A unit has no children: a command to any other target is refused, and nothing is journaled.
    [Fact]
    public async Task AUnitCommandToAChildIsRefused()
    {
        using var owner = AsOwner();
        await using var os = await UnitOnTestOS();
        var before = os.Journal(new BrainId(Scope)).Signals(NeuronId.Parse("owner/ctr/default")).Count;

        IResult result;
        using (AsPerson()) { result = await AppUiEndpoints.Act(new("owner/ctr", "other", "ctr.bump"), new TestOSBrain(os), Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current); }
        await os.Idle();

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains($"\"reason\":\"{ActivationRefusedException.UnknownChild}\"", await Body(result), StringComparison.Ordinal);
        Assert.Equal(before, os.Journal(new BrainId(Scope)).Signals(NeuronId.Parse("owner/ctr/default")).Count);
    }

    // A unit view whose command declares "by" is never published (the framework's ViewDeclaresBy).
    [Fact]
    public async Task AUnitViewDeclaringByIsNotPublished()
    {
        using var owner = AsOwner();
        await using var os = await UnitOnTestOS("forger");

        Assert.Equal(1, (await os.Get<IStateReader>(NeuronId.Parse("owner/forger/default")).Read("forger")).GetProperty("kicks").GetInt32());
        Assert.Null(await os.Get<IViewReader>(NeuronId.Parse("owner/forger/default")).Read("forger"));
    }

    // ---- a sandboxed app's view neuron ----

    private static readonly View Sandboxed = new("Tasks", new Column(
    [
        new DigitalBrain.UI.Button("save", "Save", new Command(".", "save", JsonSerializer.SerializeToElement(new { id = "t1" }))),
        new DigitalBrain.UI.Button("cancel", "Cancel", new Command(".", "cancel", JsonSerializer.SerializeToElement(new[] { "f/1", "x:y" }))),
        new DigitalBrain.UI.Button("copy", "Copy", Intent: new Intent(IntentKind.Copy, "neuron://owner/tasks")),
    ]));

    private static async Task<TestOS> SandboxedOnTestOS()
    {
        RecordingApp.Requests.Clear();
        var os = TestOS.Local().RegisterModule<FlutterModule>();
        os.Register<RecordingApp>();
        using (Edge.AsPerson()) { await os.Get<IView>(TasksView).Publish(1, Sandboxed); }
        return os;
    }

    private static async Task<IResult> ActOnSandboxed(TestOS os, string target, string signal, JsonElement? args = null, string? value = null)
    {
        var brain = new TestOSBrain(os);
        using (AsPerson())
        {
            return await AppUiEndpoints.Act(new($"{Scope}/apps/tasks", target, signal, args, value), brain, Scope, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current,
                resolver: new ScreenTargetResolver(brain, []));
        }
    }

    [Fact]
    public async Task TheResolverNamesASandboxedAppsViewNeuron()
    {
        await using var os = await SandboxedOnTestOS();
        using var person = AsPerson();

        Assert.Equal(new ScreenTarget(TasksView, IScreenTargetResolver.ViewKind), await new ScreenTargetResolver(new TestOSBrain(os), []).ResolveAsync(new BrainId(Scope), "owner/apps/tasks/view", Ct));
    }

    [Fact]
    public async Task ASandboxedAppsCommandBecomesItsRequestWithTheValueAndThePersonWhoActed()
    {
        await using var os = await SandboxedOnTestOS();

        var result = await ActOnSandboxed(os, ".", "save", JsonSerializer.SerializeToElement(new { id = "t1" }), "Ship docs");
        await os.Idle();

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(("apps/tasks", "save", """{"id":"t1","value":"Ship docs","by":"person-ann"}"""), Assert.Single(RecordingApp.Requests));
    }

    // A positional array has no member to name who acted: it reaches the app as sent, with a field value appended.
    [Fact]
    public async Task ASandboxedAppsArrayArgsReachItAsSent()
    {
        await using var os = await SandboxedOnTestOS();

        var result = await ActOnSandboxed(os, ".", "cancel", JsonSerializer.SerializeToElement(new[] { "f/1", "x:y" }), "2d");
        await os.Idle();

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(("apps/tasks", "cancel", """["f/1","x:y","2d"]"""), Assert.Single(RecordingApp.Requests));
    }

    // The view neuron sends only what its current view sends to ".": no other signal, and no child target (the app has no slots).
    [Theory]
    [InlineData(".", "delete-everything", "NoEmit")]
    [InlineData("child", "save", "NoChildren")]
    public async Task ASandboxedAppRefusesACommandItsViewDoesNotSend(string target, string signal, string reason)
    {
        await using var os = await SandboxedOnTestOS();

        var result = await ActOnSandboxed(os, target, signal);
        await os.Idle();

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains($"\"reason\":\"{reason}\"", await Body(result), StringComparison.Ordinal);
        Assert.Empty(RecordingApp.Requests);
    }

    // ---- what the edge still owns ----

    // A field's text joins the command as "value": args that already declare one are refused (400).
    [Fact]
    public void AFieldValueOverADeclaredValueIsRefused()
        => Assert.Throws<ArgumentException>(() => ViewArgs.WithValue(JsonSerializer.SerializeToElement(new { Value = "x" }), "y"));

    [Fact]
    public void AFieldValueOverANullOptionalValueSucceeds()
    {
        var command = IntoChat.OS.UI.Kit.Sends(new IntoChat.OS.Apps.Ino.InoSend());
        Assert.False(command.Args!.Value.TryGetProperty("value", out _));
        Assert.False(command.Args!.Value.TryGetProperty("Value", out _));

        var merged = ViewArgs.WithValue(JsonSerializer.SerializeToElement(new { Message = (string?)null, Value = (string?)null }), "Hello Ino");
        Assert.Equal("Hello Ino", merged.GetProperty("value").GetString());
        Assert.False(merged.TryGetProperty("Value", out _));
    }

    [Fact]
    public void AFieldValueIsAppendedToACommandsArrayArgs()
        => Assert.Equal("""["f/1","blocks","2d"]""", ViewArgs.WithValue(JsonSerializer.SerializeToElement(new[] { "f/1", "blocks" }), "2d").GetRawText());

    [Fact]
    public void AUnitViewportNamesTheFailedReaction()
    {
        var view = new PublishedView("x.watch", 3, JsonElement.Parse("""{"title": "X", "root": {"kind": "text", "value": "1 unseen"}}"""));
        var failing = new TraceEntry("owner/x.watch/default", 9, "synapse.failed", DateTimeOffset.UnixEpoch, null, null,
            """{"alias":"x.watch","function":"Units.XWatch.XWatch.Poll","cause":"time.tick","causeId":null,"error":"HttpRequestException","message":"api.x.com did not return JSON"}""");
        Assert.Equal("Poll failed: api.x.com did not return JSON", AppUiEndpoints.UnitError(view, failing));
        Assert.Null(AppUiEndpoints.UnitError(view, null));
        Assert.Equal("This unit has not published a view.", AppUiEndpoints.UnitError(null, failing));
        Assert.Equal("The last reaction failed.", AppUiEndpoints.UnitError(view, failing with { Payload = "SynapseFailed" }));
    }

    [Fact]
    public async Task AppUiEndpoints_Map_UsesServiceProviderLoggerFactoryForSwallowedViewErrors()
    {
        var provider = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(provider);
        await using var app = builder.Build();

        // Map resolves ILoggerFactory from endpoints.ServiceProvider and captures ReadNode log events
        AppUiEndpoints.Map(app);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AppUiEndpoints).FullName!);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            AppUiEndpoints.ReadNode(UIVocabulary.ViewType, "owner/apps/missing/view", new NoViews(), Scope, Ct, logger));

        Assert.Contains(provider.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Screen view could not be read.", StringComparison.Ordinal));
    }

    private static Task<string> Body(IResult result)
        => Task.FromResult(JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value, JsonSerializerOptions.Web));

    private sealed class TestOSBrain(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }

    // Every read fails, which ReadNode logs and swallows (the screen read) or lets through (the view read).
    private sealed class NoViews : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class
            => typeof(T) == typeof(IView) ? (T)(object)new FailingView() : throw new InvalidOperationException($"No {typeof(T).Name} in this fake.");
    }

    private sealed class FailingView : IView
    {
        public Task Publish(long generation, View view) => throw new NotSupportedException();
        public Task<PublishedView?> Read() => throw new NotSupportedException();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void Dispose() { }

        private sealed class Sink(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Add((logLevel, formatter(state, exception)));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
