using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Spi;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.TextField;

namespace IntoChat.OS.Modules.Flutter;

// The shell's UI events through the edge's one dispatch, against recording neurons in place of the brain.
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

    [Fact]
    public async Task AForeignNeuronButtonReachesThatNeuron()
    {
        var brain = new RecordingBrain();
        var action = NeuronButtonAction.Format(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-9", "false"]);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/ino/main", action, Revision: 1, Field: "approve"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/confirmations", "plan-9", false), Assert.Single(brain.Inbox.Resolved));
        Assert.Empty(brain.Views);
    }

    [Fact]
    public async Task ApproveButtonReachesTheConfirmationsInbox()
    {
        var brain = new RecordingBrain();
        // The Tasks and Ino approve action as the presentation writes it.
        var action = NeuronButtonAction.Format(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-1", "true"]);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/tasks/side", action), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/confirmations", "plan-1", true), Assert.Single(brain.Inbox.Resolved));
    }

    [Fact]
    public async Task AModelAuthoredAppViewCannotResolveAConfirmation()
    {
        var brain = new RecordingBrain();
        var action = NeuronButtonAction.Format(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-1", "true"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/dismisser/view", action), brain, Scope, Hosted, Ct));
        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task AnAppNamingACompiledTypeButNotHostLoadedCannotResolveAConfirmation()
    {
        var brain = new RecordingBrain();
        brain.TrustedApps.Add("apps/spoof");
        var action = NeuronButtonAction.Format(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-1", "true"]);

        Assert.True(AppTrust.IsOsTrusted(await brain.Get<IApp>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId(Scope), "app", "apps/spoof")).Read()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/spoof/view", action), brain, Scope, Hosted, Ct));
        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task TheConfirmationInboxSurfaceStillResolves()
    {
        var brain = new RecordingBrain();

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent(UIVocabulary.ConfirmationType, IConfirmationInbox.Key(Scope), $"{DigitalBrain.UI.Confirmation.ConfirmView.ResolveOperation}:plan-3:true"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/confirmations", "plan-3", true), Assert.Single(brain.Inbox.Resolved));
    }

    [Fact]
    public async Task ANeuronButtonToAnyOtherTargetIsRefused()
    {
        var brain = new RecordingBrain();
        var action = NeuronButtonAction.Format("grants/default", "Revoke", ["g1"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/tasks/side", action), brain, Scope, Hosted, Ct));
        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task TypingIntoAnAppFieldUpdatesItsFieldNeuron()
    {
        var brain = new RecordingBrain();

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("textfield", "owner/apps/tasks/title", Value: "Ship docs"), brain, Scope, Hosted, Ct);

        Assert.Equal(("owner/apps/tasks/title", "Ship docs"), Assert.Single(brain.Field.Inputs));
    }

    private sealed class RecordingBrain : INeuronProxies
    {
        public RecordingInbox Inbox { get; } = new();
        public RecordingField Field { get; } = new();
        public List<string> Views { get; } = [];
        // Apps whose settings name a compiled type.
        public HashSet<string> TrustedApps { get; } = ["apps/ino", "apps/tasks"];

        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IConfirmationInbox)) { Inbox.Key = id.Brain.Value + "/" + id.Key; return (T)(object)Inbox; }
            if (typeof(T) == typeof(IApp)) { return (T)(object)FakeApp.For(TrustedApps.Contains(id.Key)); }
            if (typeof(T) == typeof(ITextField)) { Field.Key = id.Brain.Value + "/" + id.Key; return (T)(object)Field; }
            Views.Add(id.ToString());
            throw new InvalidOperationException($"No {typeof(T).Name} in this fake.");
        }
    }

    private sealed class RecordingInbox : IConfirmationInbox
    {
        public string Key { get; set; } = "";
        public List<(string Key, string PlanId, bool Approve)> Resolved { get; } = [];
        public Task<ConfirmationInbox> Read() => Task.FromResult(new ConfirmationInbox(0, []));
        public Task Resolve(string planId, bool approve) { Resolved.Add((Key, planId, approve)); return Task.CompletedTask; }
    }

    private sealed class RecordingField : ITextField
    {
        public string Key { get; set; } = "";
        public List<(string Key, string Value)> Inputs { get; } = [];
        public Task Configure(string label, string kind, string? submitButton = null) => Task.CompletedTask;
        public Task SetValue(string value) => Task.CompletedTask;
        public Task<bool> TrySetValue(string value, int expectedVersion) => Task.FromResult(true);
        public Task Input(string value) { Inputs.Add((Key, value)); return Task.CompletedTask; }
        public Task<TextFieldState> Read() => Task.FromResult(new TextFieldState());
    }

    private class FakeApp : System.Reflection.DispatchProxy
    {
        private bool _compiled;

        public static IApp For(bool compiled)
        {
            var app = Create<IApp, FakeApp>();
            ((FakeApp)(object)app)._compiled = compiled;
            return app;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IApp.Read)) { throw new NotSupportedException(method?.Name); }
            var settings = _compiled ? new Dictionary<string, string> { [AppTrust.CompiledTypeSetting] = "Os.App" } : [];
            return Task.FromResult(new AppSnapshot("app", new("", "", [], [], []), "", [], settings, [], "1.0", AppStatus.Live, [], []));
        }
    }
}
