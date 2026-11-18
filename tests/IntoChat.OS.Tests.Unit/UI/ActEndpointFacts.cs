using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Flutter;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IntoChat.OS.UI;

// POST /brains/{brainId}/act: the request names a top-level screen and a target relative to it; the parent's Dispatch gates its children.
public sealed class ActEndpointFacts
{
    private const string Scope = "owner";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Caller? Alice => IntoChat.OS.Tests.TestEdge.Authority.Current;

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId(Scope)));

    private sealed class ParentScreen : IScreen
    {
        public string[] Shown { get; init; } = ["dark-mode"];
        public List<Command> Dispatched { get; } = [];
        public Task<PublishedView?> Open() => Task.FromResult<PublishedView?>(null);
        public Task Act(ScreenAction action) => Task.CompletedTask;
        public Task<PublishedView?> Read(string? alias = null) => Task.FromResult<PublishedView?>(null);
        public Task Activate(string alias, long generation, string nodeKey, string? fieldValue = null, string? by = null) => Task.CompletedTask;
        public Task Request(string signal, JsonElement? args = null) => Task.CompletedTask;
        public Task Dispatch(Command command)
        {
            if (command.Target != "." && !Shown.Contains(command.Target)) { return Task.FromException(new ActivationRefusedException(ActivationRefusedException.UnknownChild)); }
            Dispatched.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class Brain(IScreen parent) : INeuronProxies
    {
        public List<NeuronId> Gets { get; } = [];
        public T Get<T>(NeuronId id) where T : class
        {
            Gets.Add(id);
            return id.Type == "app.settings" && id.Key == "default" ? (T)parent : throw new InvalidOperationException($"No neuron {id}.");
        }
    }

    [Fact]
    public async Task Act_runs_request_on_the_address()
    {
        using var _ = AsPerson();
        var parent = new ParentScreen();
        var brain = new Brain(parent);
        var args = JsonSerializer.SerializeToElement(new { on = true });

        var result = await AppUiEndpoints.Act(new("owner/apps/settings", "dark-mode", "toggle.set", args), brain, Scope, Ct, Alice);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var sent = Assert.Single(parent.Dispatched);
        Assert.Equal(("dark-mode", "toggle.set"), (sent.Target, sent.Signal));
        Assert.Equal(new NeuronId(new BrainId(Scope), "app.settings", "default"), Assert.Single(brain.Gets));
    }

    [Fact]
    public async Task Act_on_unknown_address_is_409_with_reason()
    {
        using var _ = AsPerson();
        var parent = new ParentScreen();
        var brain = new Brain(parent);

        var result = await AppUiEndpoints.Act(new("owner/apps/settings", "ghost", "toggle.set"), brain, Scope, Ct, Alice);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains(ActivationRefusedException.UnknownChild, JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value), StringComparison.Ordinal);
        Assert.Empty(parent.Dispatched);
        Assert.Single(brain.Gets); // only the parent: no child neuron is reached or created
    }

    [Theory]
    [InlineData("owner/app.settings/owner/app.settings/default/dark-mode")]
    [InlineData("owner/apps/settings/dark-mode")]
    [InlineData("other/apps/settings")]
    public async Task Act_on_a_raw_child_or_foreign_neuron_is_refused(string neuron)
    {
        using var _ = AsPerson();
        var brain = new Brain(new ParentScreen());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Act(new(neuron, ".", "toggle.set"), brain, Scope, Ct, Alice));

        Assert.Empty(brain.Gets);
    }

    [Fact]
    public async Task Act_needs_a_signed_in_person()
    {
        var brain = new Brain(new ParentScreen());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AppUiEndpoints.Act(new("owner/apps/settings", ".", "toggle.set"), brain, Scope, Ct, caller: null));
    }
}
