using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Errors;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IntoChat.OS.Modules.Flutter;

public sealed class AppUiUnitOwnershipFacts
{
    private const string Scope = "owner";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDisposable AsPerson(string principal = "alice")
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person(principal, principal, new BrainId(Scope)));

    [Theory]
    [InlineData("owner/units/counter", true)]
    [InlineData("owner/units/counter/view", true)]
    [InlineData("owner/counter", true)]
    [InlineData("owner/counter/view", true)]
    [InlineData("owner/apps/calculator", true)]
    [InlineData("owner/applications/app1", true)]
    [InlineData("owner/images/img1", true)]
    [InlineData("foreign/units/counter", false)]
    [InlineData("foreign/counter", false)]
    [InlineData("foreign/apps/calculator", false)]
    public void OwnsUi_ValidatesBrainScopeCorrectly(string path, bool expected)
    {
        var result = AppUiEndpoints.OwnsUi(Scope, path);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("owner/counter", "counter")]
    [InlineData("owner/units/counter", "counter")]
    [InlineData("owner/units/counter/view", "counter")]
    [InlineData("owner/my-unit.v1", "my-unit.v1")]
    [InlineData("owner/apps", null)]
    [InlineData("owner/units", null)]
    [InlineData("owner/images", null)]
    [InlineData("other/counter", null)]
    public void UnitAlias_ExtractsAliasCorrectly(string name, string? expected)
    {
        var result = AppUiEndpoints.UnitAlias(Scope, name);
        Assert.Equal(expected, result);
    }

    private sealed class MockBrain : INeuronProxies
    {
        private readonly Dictionary<(string Type, string Key), object> _neurons = new();

        public void Register<T>(NeuronId id, T instance) where T : class
        {
            _neurons[(id.Type, id.Key)] = instance;
        }

        public T Get<T>(NeuronId id) where T : class
        {
            if (_neurons.TryGetValue((id.Type, id.Key), out var instance))
            {
                return (T)instance;
            }
            throw new InvalidOperationException($"No neuron registered for {id.Type}:{id.Key}");
        }
    }

    private sealed class FakeUnitScreen : IScreen, IViewReader
    {
        private readonly PublishedView _published;
        public List<(string Alias, long Generation, string Key, string? Value, string? By)> Activations { get; } = [];

        public FakeUnitScreen(PublishedView published) => _published = published;

        public Task<PublishedView?> Open() => Read();

        public Task Act(ScreenAction action) => Activate(action.Alias ?? _published.Alias, action.Generation, action.NodeKey, action.FieldValue);

        public Task<PublishedView?> Read(string? alias = null) => Task.FromResult<PublishedView?>(_published);

        public Task Activate(string alias, long generation, string nodeKey, string? fieldValue = null, string? by = null)
        {
            if (generation != _published.Generation) { throw new ActivationRefusedException(ActivationRefusedException.Stale); }
            Activations.Add((alias, generation, nodeKey, fieldValue, by));
            return Task.CompletedTask;
        }

        Task IViewReader.Activate(string alias, long generation, string nodeKey, string? fieldValue, string? by)
            => Activate(alias, generation, nodeKey, fieldValue, by);

        public Task Request(string signal, JsonElement? args = null) => Task.CompletedTask;
    }

    private sealed class FakeHostedTypes : IHostedAppTypes
    {
        public bool IsHostLoaded(string appKey) => true;
    }

    [Fact]
    public async Task ReadNode_RuntimeUnit_ViaScreen_ReturnsPublishedView()
    {
        var brain = new MockBrain();
        var view = new View("Counter", new Column([new Button("btn-bump", "Bump", new Emit("counter", "bump", null))]));
        var json = JsonSerializer.SerializeToElement(view, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
        var published = new PublishedView("counter", 1L, json);

        var screen = new FakeUnitScreen(published);
        brain.Register<IScreen>(new NeuronId(new BrainId(Scope), "counter", "default"), screen);

        // Read node by unit path "owner/units/counter"
        var result = await AppUiEndpoints.ReadNode("view", "owner/units/counter", brain, Scope, Ct);
        Assert.NotNull(result);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var resJson = JsonSerializer.Serialize(valueResult.Value, JsonSerializerOptions.Web);
        Assert.Contains("\"alias\":\"counter\"", resJson);
        Assert.Contains("\"generation\":1", resJson);
    }

    [Fact]
    public async Task DeliverView_RuntimeUnit_ActivatesScreen()
    {
        var brain = new MockBrain();
        var view = new View("Counter", new Column([new Button("btn-bump", "Bump", new Emit("counter", "bump", null))]));
        var json = JsonSerializer.SerializeToElement(view, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
        var published = new PublishedView("counter", 1L, json);

        var screen = new FakeUnitScreen(published);
        brain.Register<IScreen>(new NeuronId(new BrainId(Scope), "counter", "default"), screen);

        var uiEvent = new AppUiEndpoints.UiEvent("view", "owner/units/counter", Generation: 1L, Key: "btn-bump");

        using (AsPerson("bob"))
        {
            var result = await AppUiEndpoints.Deliver(uiEvent, brain, Scope, new FakeHostedTypes(), Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);
            Assert.NotNull(result);
        }

        var act = Assert.Single(screen.Activations);
        Assert.Equal("counter", act.Alias);
        Assert.Equal("btn-bump", act.Key);
        Assert.Equal("bob", act.By);
    }
}
