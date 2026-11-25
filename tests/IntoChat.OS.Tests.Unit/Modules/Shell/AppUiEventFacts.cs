using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// The shell's UI events through the edge's one dispatch.
public sealed class AppUiEventFacts
{
    private const string Scope = "owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The OS's own apps, loaded by the host; any other app here is model-authored.
    private static readonly HostedTypes Hosted = new("owner/apps/ino", "owner/apps/tasks");

    private sealed class HostedTypes(params string[] loaded) : IntoChat.OS.Host.Kernel.IHostedAppTypes
    {
        public bool IsHostLoaded(string appKey) => loaded.Contains(appKey);
    }

    // The person the HTTP edge stamps for a signed-in caller.
    private static IDisposable AsPerson() => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("person-ann", "person-ann", new BrainId(Scope)));

    private static AppUiEndpoints.UiEvent View(string name, string key, string? value = null) => new("view", name, Value: value, Generation: 1, Key: key);

    [Fact]
    public async Task ActivateRoutesEmitToACompiledAppAsAppRequested()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("tasks", "cancel", JsonSerializer.SerializeToElement(new[] { "f/1", "x:y" }))));

        await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "cancel"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(("owner/apps/tasks", "cancel", "[\"f/1\",\"x:y\"]"), Assert.Single(brain.Emitted));
        Assert.Equal(("owner/apps/tasks/view", 1L, "cancel"), Assert.Single(brain.Activations));
    }

    [Fact]
    public async Task AFieldValueJoinsTheEmitsArgsAsValue()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("tasks", "rename", JsonSerializer.SerializeToElement(new { id = "t1" }))));

        await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "title", "Ship docs"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(("owner/apps/tasks", "rename", "{\"id\":\"t1\",\"value\":\"Ship docs\"}"), Assert.Single(brain.Emitted));
    }

    [Fact]
    public async Task AnEmitToAnotherOwnerIsRefused()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("ino", "send")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "x"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
        Assert.Empty(brain.Emitted);
    }

    [Fact]
    public async Task A_command_to_a_child_of_a_slotless_app_is_refused_with_NoSlots()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Command, Command: new Command("child", "toggle")));

        var result = await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "x"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains($"\"reason\":\"{ActivationRefusedException.NoSlots}\"", await Body(result), StringComparison.Ordinal);
        Assert.Empty(brain.Emitted);
    }

    [Fact]
    public void Only_an_info_bar_close_resolves_to_an_action()
    {
        using var stray = JsonDocument.Parse("""{"kind":"button","key":"b","label":"B","close":{"target":".","signal":"x"}}""");
        using var bar = JsonDocument.Parse("""{"kind":"infoBar","key":"i","title":"T","message":"M","close":{"target":".","signal":"dismissed"}}""");

        Assert.Equal(ViewActivationKind.None, ViewValidation.ActionOf(stray.RootElement, "owner/apps/tasks").Kind);
        Assert.Equal(("owner/apps/tasks", "dismissed"), (ViewValidation.ActionOf(bar.RootElement, "owner/apps/tasks").Emit!.Owner, ViewValidation.ActionOf(bar.RootElement, "owner/apps/tasks").Emit!.Signal));
    }

    [Fact]
    public async Task ActivateReturnsAnIntentToTheClient()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Intent, Intent: new Intent(IntentKind.OpenWindow, "os://window/open?app=trace")));

        var result = await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "open"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal("{\"intent\":{\"kind\":\"OpenWindow\",\"arg\":\"os://window/open?app=trace\"}}", await Body(result));
        Assert.Empty(brain.Emitted);
    }

    [Fact]
    public async Task ActivateRefusesResolveFromANonHostLoadedOwner()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Resolve, Resolve: new Resolve("plan-1")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/dismisser/view", "approval", "approve"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/spoof/view", "approval", "approve"), brain, Scope, null, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task HostLoadedAppViewCannotResolveConfirmationViaViewEvent()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Resolve, Resolve: new Resolve("plan-1")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/ino/view", "approval", "approve"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/tasks/side", "approval", "deny"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task ActivateRoutesEmitToAUnitAlias()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = TestOS.Local().Register(typeof(CounterUnit).Assembly);
        os.Confirmations.ApproveAll();
        await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).Install(new DigitalBrain.Units.InstallRequest("ctr", new Dictionary<string, string>()), Ct);
        await os.Idle();
        await os.Get<IKicker>(NeuronId.Parse("owner/kicker/a")).Kick();
        await os.Idle();
        var brain = new TestOSBrain(os);
        var reader = os.Get<IViewReader>(NeuronId.Parse("owner/ctr/default"));
        var shown = await reader.Read("ctr");
        Assert.NotNull(shown);

        using (AsPerson()) { await AppUiEndpoints.Deliver(new("view", "owner/ctr", Value: "hi", Generation: shown.Generation, Key: "bump"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current); }
        await os.Idle();
        var copy = await AppUiEndpoints.Deliver(new("view", "owner/ctr", Generation: (await reader.Read("ctr"))!.Generation, Key: "copy"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        var state = await os.Get<IStateReader>(NeuronId.Parse("owner/ctr/default")).Read("ctr");
        Assert.Equal(1, state.GetProperty("bumps").GetInt32());
        Assert.Equal("hi", state.GetProperty("value").GetString());
        Assert.Contains("\"kind\":\"Copy\"", await Body(copy), StringComparison.Ordinal);
        Assert.Empty(os.Failures);
    }

    private static async Task<(TestOS Os, IViewReader Reader, PublishedView Shown)> UnitOnTestOS()
    {
        var os = TestOS.Local().Register(typeof(CounterUnit).Assembly);
        os.Confirmations.ApproveAll();
        await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).Install(new DigitalBrain.Units.InstallRequest("ctr", new Dictionary<string, string>()), Ct);
        await os.Idle();
        await os.Get<IKicker>(NeuronId.Parse("owner/kicker/a")).Kick();
        await os.Idle();
        var reader = os.Get<IViewReader>(NeuronId.Parse("owner/ctr/default"));
        return (os, reader, (await reader.Read("ctr"))!);
    }

    [Fact]
    public async Task AUnitViewEmitWithAForeignOwnerIsRefused()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var (os, _, shown) = await UnitOnTestOS();
        await using var _os = os;
        var before = os.Journal(new BrainId("owner")).Signals(NeuronId.Parse("owner/ctr/default")).Count;

        using (AsPerson()) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new("view", "owner/ctr", Generation: shown.Generation, Key: "foreign"), new TestOSBrain(os), Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current)); }
        await os.Idle();

        Assert.Equal(before, os.Journal(new BrainId("owner")).Signals(NeuronId.Parse("owner/ctr/default")).Count);
        Assert.Equal(0, (await os.Get<IStateReader>(NeuronId.Parse("owner/ctr/default")).Read("ctr")).GetProperty("bumps").GetInt32());
    }

    [Fact]
    public async Task AResolveNodeInAUnitViewIsRefused()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var (os, _, shown) = await UnitOnTestOS();
        await using var _os = os;
        var brain = new UnitAndInbox(os);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new("view", "owner/ctr", Value: "approve", Generation: shown.Generation, Key: "approval"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));

        Assert.Empty(brain.Inbox.Resolved);
    }

    // Only the edge names who acted: it stamps "by" with the caller's person id, whatever the field's text says.
    [Fact]
    public async Task AUnitActivationIsStampedWithThePersonWhoActed()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var (os, _, shown) = await UnitOnTestOS();
        await using var _os = os;

        using (AsPerson())
        { await AppUiEndpoints.Deliver(new("view", "owner/ctr", Value: "{\"by\":\"mallory\"}", Generation: shown.Generation, Key: "bump"), new TestOSBrain(os), Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current); }
        await os.Idle();

        Assert.Equal("person-ann", (await os.Get<IStateReader>(NeuronId.Parse("owner/ctr/default")).Read("ctr")).GetProperty("by").GetString());
    }

    // No person, no author: an unstamped activation is refused before anything is journaled.
    [Fact]
    public async Task AUnitActivationWithoutAPersonIsRefused()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var (os, _, shown) = await UnitOnTestOS();
        await using var _os = os;

        // The edge stamped no person for this request: the person above only installed the unit.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new("view", "owner/ctr", Generation: shown.Generation, Key: "bump"), new TestOSBrain(os), Scope, Hosted, Ct, caller: null));
        await os.Idle();

        Assert.Equal(0, (await os.Get<IStateReader>(NeuronId.Parse("owner/ctr/default")).Read("ctr")).GetProperty("bumps").GetInt32());
    }

    // A unit view whose emit declares "by" is never published (the framework's ViewDeclaresBy), so the edge has nothing to activate.
    [Fact]
    public async Task AUnitViewDeclaringByIsNotPublished()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = TestOS.Local().Register(typeof(CounterUnit).Assembly);
        os.Confirmations.ApproveAll();
        await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).Install(new DigitalBrain.Units.InstallRequest("forger", new Dictionary<string, string>()), Ct);
        await os.Idle();
        await os.Get<IKicker>(NeuronId.Parse("owner/kicker/a")).Kick();
        await os.Idle();

        Assert.Equal(1, (await os.Get<IStateReader>(NeuronId.Parse("owner/forger/default")).Read("forger")).GetProperty("kicks").GetInt32());
        Assert.Null(await os.Get<IViewReader>(NeuronId.Parse("owner/forger/default")).Read("forger"));
    }

    // A field's text joins the emit as "value": args that already declare one are refused (400), on a unit view and an app view.
    [Fact]
    public async Task AFieldValueOverADeclaredValueIsRefused()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var (os, _, shown) = await UnitOnTestOS();
        await using var _os = os;
        await Assert.ThrowsAsync<ArgumentException>(() => AppUiEndpoints.Deliver(new("view", "owner/ctr", Value: "typed", Generation: shown.Generation, Key: "valued"), new TestOSBrain(os), Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
        Assert.Equal(0, (await os.Get<IStateReader>(NeuronId.Parse("owner/ctr/default")).Read("ctr")).GetProperty("bumps").GetInt32());

        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("tasks", "rename", JsonSerializer.SerializeToElement(new { value = "declared" }))));
        await Assert.ThrowsAsync<ArgumentException>(() => AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "title", "typed"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
        Assert.Empty(brain.Emitted);
        Assert.Throws<ArgumentException>(() => ViewArgs.WithValue(JsonSerializer.SerializeToElement(new { Value = "x" }), "y"));
    }

    [Fact]
    public async Task AFieldValueOverANullOptionalValueSucceeds()
    {
        var emit = IntoChat.OS.Apps.AppSignals.Emits("ino", new IntoChat.OS.Apps.Ino.InoSend());
        Assert.False(emit.Args!.Value.TryGetProperty("value", out _));
        Assert.False(emit.Args!.Value.TryGetProperty("Value", out _));

        var merged = ViewArgs.WithValue(JsonSerializer.SerializeToElement(new { Message = (string?)null, Value = (string?)null }), "Hello Ino");
        Assert.Equal("Hello Ino", merged.GetProperty("value").GetString());
        Assert.False(merged.TryGetProperty("Value", out _));
    }

    [Fact]
    public async Task AFieldValueIsAppendedToAnEmitsArrayArgs()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("tasks", "relate", JsonSerializer.SerializeToElement(new[] { "f/1", "blocks" }))));

        await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "lag", "2d"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(("owner/apps/tasks", "relate", "[\"f/1\",\"blocks\",\"2d\"]"), Assert.Single(brain.Emitted));
    }

    [Fact]
    public async Task TypingIntoAnAppFieldUpdatesItsFieldNeuron()
    {
        var brain = new RecordingBrain(ViewActivation.None);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("field", "owner/apps/tasks/title", Value: "Ship docs"), brain, Scope, Hosted, Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);

        Assert.Equal(("owner/apps/tasks/title", "Ship docs"), Assert.Single(brain.Field.Inputs));
    }

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

        // Map resolves ILoggerFactory from endpoints.ServiceProvider and captures ReadNode/Deliver log events
        AppUiEndpoints.Map(app);
        var resolvedFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var brain = new RecordingBrain(ViewActivation.None);
        var logger = resolvedFactory.CreateLogger(typeof(AppUiEndpoints).FullName!);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            AppUiEndpoints.ReadNode(UIVocabulary.ViewType, "owner/apps/missing/view", brain, Scope, Ct, logger));

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

    // The unit on TestOS, with a recording inbox in place of the confirmations.
    private sealed class UnitAndInbox(TestOS os) : INeuronProxies
    {
        public RecordingInbox Inbox { get; } = new();
        public T Get<T>(NeuronId id) where T : class => typeof(T) == typeof(IConfirmationInbox) ? (T)(object)Inbox : os.Get<T>(id);
    }

    private sealed class RecordingBrain(ViewActivation activation) : INeuronProxies
    {
        public RecordingInbox Inbox { get; } = new();
        public RecordingField Field { get; } = new();
        public List<(string App, string Signal, string Args)> Emitted { get; } = [];
        public List<(string View, long Generation, string Key)> Activations { get; } = [];

        public T Get<T>(NeuronId id) where T : class
        {
            var key = id.Brain.Value + "/" + id.Key;
            if (typeof(T) == typeof(IConfirmationInbox)) { Inbox.Key = key; return (T)(object)Inbox; }
            if (typeof(T) == typeof(IApp)) { return (T)(object)FakeApp.For(key, this); }
            if (typeof(T) == typeof(IField)) { Field.Key = key; return (T)(object)Field; }
            if (typeof(T) == typeof(IView)) { return (T)(object)new FakeView(key, this, activation); }
            throw new InvalidOperationException($"No {typeof(T).Name} in this fake.");
        }
    }

    private sealed class FakeView(string key, RecordingBrain brain, ViewActivation activation) : IView
    {
        public Task Publish(long generation, View view) => throw new NotSupportedException();
        public Task<PublishedView?> Read() => throw new NotSupportedException();
        public Task<ViewActivation> Activate(long generation, string nodeKey, string? fieldValue)
        {
            brain.Activations.Add((key, generation, nodeKey));
            return Task.FromResult(activation with { Value = fieldValue });
        }
    }

    private sealed class RecordingInbox : IConfirmationInbox
    {
        public string Key { get; set; } = "";
        public List<(string Key, string PlanId, bool Approve)> Resolved { get; } = [];
        public Task<ConfirmationInbox> Read() => Task.FromResult(new ConfirmationInbox(0, []));
        public Task Resolve(string planId, bool approve) { Resolved.Add((Key, planId, approve)); return Task.CompletedTask; }
    }

    private sealed class RecordingField : IField
    {
        public string Key { get; set; } = "";
        public List<(string Key, string Value)> Inputs { get; } = [];
        public Task Configure(FieldKind kind) => Task.CompletedTask;
        public Task Set(string? value) => Task.CompletedTask;
        public Task Input(string value) { Inputs.Add((Key, value)); return Task.CompletedTask; }
        public Task<string?> TakeSecret() => Task.FromResult<string?>(null);
        public Task<FieldState> Read() => Task.FromResult(new FieldState());
    }

    private class FakeApp : System.Reflection.DispatchProxy
    {
        private string _key = "";
        private RecordingBrain? _brain;

        public static IApp For(string key, RecordingBrain brain)
        {
            var app = Create<IApp, FakeApp>();
            ((FakeApp)(object)app)._key = key;
            ((FakeApp)(object)app)._brain = brain;
            return app;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IApp.Emit))
            {
                _brain!.Emitted.Add((_key, (string)args![0]!, (string)args[1]!));
                return Task.FromResult(Guid.NewGuid());
            }
            if (method?.Name != nameof(IApp.Read)) { throw new NotSupportedException(method?.Name); }
            return Task.FromResult(new AppSnapshot("app", new("", "", [], [], []), "", [], [], [], "1.0", AppStatus.Live, [], []));
        }
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
