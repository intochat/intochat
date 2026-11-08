using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Spi;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.AspNetCore.Http;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// The shell's UI events through the edge's one dispatch.
public sealed class AppUiEventFacts
{
    private const string Scope = "owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The OS's own apps, loaded by the host; any other app here is model-authored.
    private static readonly HostedTypes Hosted = new("owner/apps/ino", "owner/apps/tasks");

    private sealed class HostedTypes(params string[] loaded) : IntoChat.OS.Framework.IHostedAppTypes
    {
        public bool IsHostLoaded(string appKey) => loaded.Contains(appKey);
    }

    private static AppUiEndpoints.UiEvent View(string name, string key, string? value = null) => new("view", name, Value: value, Generation: 1, Key: key);

    [Fact]
    public async Task ActivateRoutesEmitToACompiledAppAsAppRequested()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("tasks", "cancel", JsonSerializer.SerializeToElement(new[] { "f/1", "x:y" }))));

        await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "cancel"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/apps/tasks", "cancel", "[\"f/1\",\"x:y\"]"), Assert.Single(brain.Emitted));
        Assert.Equal(("owner/apps/tasks/view", 1L, "cancel"), Assert.Single(brain.Activations));
    }

    [Fact]
    public async Task AFieldValueJoinsTheEmitsArgsAsValue()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("tasks", "rename", JsonSerializer.SerializeToElement(new { id = "t1" }))));

        await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "title", "Ship docs"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/apps/tasks", "rename", "{\"id\":\"t1\",\"value\":\"Ship docs\"}"), Assert.Single(brain.Emitted));
    }

    [Fact]
    public async Task AnEmitToAnotherOwnerIsRefused()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("ino", "send")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "x"), brain, Scope, Hosted, Ct));
        Assert.Empty(brain.Emitted);
    }

    [Fact]
    public async Task ActivateReturnsAnIntentToTheClient()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Intent, Intent: new Intent(IntentKind.OpenWindow, "os://window/open?app=trace")));

        var result = await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "open"), brain, Scope, Hosted, Ct);

        Assert.Equal("{\"intent\":{\"kind\":\"OpenWindow\",\"arg\":\"os://window/open?app=trace\"}}", await Body(result));
        Assert.Empty(brain.Emitted);
    }

    [Fact]
    public async Task AHostLoadedAppsApprovalReachesTheConfirmationsInbox()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Resolve, Resolve: new Resolve("plan-1")));

        await AppUiEndpoints.Deliver(View("owner/apps/tasks/side", "approval", "approve"), brain, Scope, Hosted, Ct);
        await AppUiEndpoints.Deliver(View("owner/apps/ino/view", "approval", "deny"), brain, Scope, Hosted, Ct);

        Assert.Equal([("owner/confirmations", "plan-1", true), ("owner/confirmations", "plan-1", false)], brain.Inbox.Resolved);
    }

    [Fact]
    public async Task ActivateRefusesResolveFromANonHostLoadedOwner()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Resolve, Resolve: new Resolve("plan-1")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/dismisser/view", "approval", "approve"), brain, Scope, Hosted, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(View("owner/apps/spoof/view", "approval", "approve"), brain, Scope, null, Ct));
        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task ActivateRoutesEmitToAUnitAlias()
    {
        await using var os = TestOS.Local().Register(typeof(CounterUnit).Assembly);
        os.Confirmations.ApproveAll();
        await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).Install(new DigitalBrain.InstallRequest("ctr", new Dictionary<string, string>()), Ct);
        await os.Idle();
        await os.Get<IKicker>(NeuronId.Parse("owner/kicker/a")).Kick();
        await os.Idle();
        var brain = new TestOSBrain(os);
        var reader = os.Get<IViewReader>(NeuronId.Parse("owner/ctr/default"));
        var shown = await reader.Read("ctr");
        Assert.NotNull(shown);

        await AppUiEndpoints.Deliver(new("view", "owner/ctr", Value: "hi", Generation: shown.Generation, Key: "bump"), brain, Scope, Hosted, Ct);
        await os.Idle();
        var copy = await AppUiEndpoints.Deliver(new("view", "owner/ctr", Generation: (await reader.Read("ctr"))!.Generation, Key: "copy"), brain, Scope, Hosted, Ct);

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
        await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).Install(new DigitalBrain.InstallRequest("ctr", new Dictionary<string, string>()), Ct);
        await os.Idle();
        await os.Get<IKicker>(NeuronId.Parse("owner/kicker/a")).Kick();
        await os.Idle();
        var reader = os.Get<IViewReader>(NeuronId.Parse("owner/ctr/default"));
        return (os, reader, (await reader.Read("ctr"))!);
    }

    [Fact]
    public async Task AUnitViewEmitWithAForeignOwnerIsRefused()
    {
        var (os, _, shown) = await UnitOnTestOS();
        await using var _os = os;
        var before = os.Journal(new BrainId("owner")).Signals(NeuronId.Parse("owner/ctr/default")).Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new("view", "owner/ctr", Generation: shown.Generation, Key: "foreign"), new TestOSBrain(os), Scope, Hosted, Ct));
        await os.Idle();

        Assert.Equal(before, os.Journal(new BrainId("owner")).Signals(NeuronId.Parse("owner/ctr/default")).Count);
        Assert.Equal(0, (await os.Get<IStateReader>(NeuronId.Parse("owner/ctr/default")).Read("ctr")).GetProperty("bumps").GetInt32());
    }

    [Fact]
    public async Task AResolveNodeInAUnitViewIsRefused()
    {
        var (os, _, shown) = await UnitOnTestOS();
        await using var _os = os;
        var brain = new UnitAndInbox(os);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new("view", "owner/ctr", Value: "approve", Generation: shown.Generation, Key: "approval"), brain, Scope, Hosted, Ct));

        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task AFieldValueIsAppendedToAnEmitsArrayArgs()
    {
        var brain = new RecordingBrain(new(ViewActivationKind.Emit, Emit: new Emit("tasks", "relate", JsonSerializer.SerializeToElement(new[] { "f/1", "blocks" }))));

        await AppUiEndpoints.Deliver(View("owner/apps/tasks/view", "lag", "2d"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/apps/tasks", "relate", "[\"f/1\",\"blocks\",\"2d\"]"), Assert.Single(brain.Emitted));
    }

    [Fact]
    public async Task TheConfirmationInboxSurfaceStillResolves()
    {
        var brain = new RecordingBrain(ViewActivation.None);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent(UIVocabulary.ConfirmationType, IConfirmationInbox.Key(Scope), $"{DigitalBrain.UI.Confirmation.ConfirmView.ResolveOperation}:plan-3:true"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/confirmations", "plan-3", true), Assert.Single(brain.Inbox.Resolved));
    }

    [Fact]
    public async Task TypingIntoAnAppFieldUpdatesItsFieldNeuron()
    {
        var brain = new RecordingBrain(ViewActivation.None);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("field", "owner/apps/tasks/title", Value: "Ship docs"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/apps/tasks/title", "Ship docs"), Assert.Single(brain.Field.Inputs));
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
}
