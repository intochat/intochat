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

    [Fact]
    public async Task AForeignNeuronButtonReachesThatNeuron()
    {
        var brain = new RecordingBrain();
        var action = NeuronButtonAction.Format(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-9", "false"]);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/ino/main", action, Revision: 1, Field: "approve"), brain, Scope, Ct);

        Assert.Equal(("owner/confirmations", "plan-9", false), Assert.Single(brain.Inbox.Resolved));
        Assert.Empty(brain.Views);
    }

    [Fact]
    public async Task ApproveButtonReachesTheConfirmationsInbox()
    {
        var brain = new RecordingBrain();
        // The Tasks and Ino approve action as the presentation writes it.
        var action = NeuronButtonAction.Format(IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation, ["plan-1", "true"]);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/tasks/side", action), brain, Scope, Ct);

        Assert.Equal(("owner/confirmations", "plan-1", true), Assert.Single(brain.Inbox.Resolved));
    }

    [Fact]
    public async Task ANeuronButtonToAnyOtherTargetIsRefused()
    {
        var brain = new RecordingBrain();
        var action = NeuronButtonAction.Format("grants/default", "Revoke", ["g1"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("view", "owner/apps/tasks/side", action), brain, Scope, Ct));
        Assert.Empty(brain.Inbox.Resolved);
    }

    [Fact]
    public async Task TypingIntoAnAppFieldUpdatesItsFieldNeuron()
    {
        var brain = new RecordingBrain();

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("textfield", "owner/apps/tasks/title", Value: "Ship docs"), brain, Scope, Ct);

        Assert.Equal(("owner/apps/tasks/title", "Ship docs"), Assert.Single(brain.Field.Inputs));
    }

    private sealed class RecordingBrain : INeuronProxies
    {
        public RecordingInbox Inbox { get; } = new();
        public RecordingField Field { get; } = new();
        public List<string> Views { get; } = [];

        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IConfirmationInbox)) { Inbox.Key = id.Brain.Value + "/" + id.Key; return (T)(object)Inbox; }
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
}
