using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests;

public sealed record ActProbeState
{
    public string Value { get; init; } = "";
    public string By { get; init; } = "";
}

[Signal("act-probe.save")]
public sealed record ActProbeSave(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

// A screen with one field whose submit names the screen itself, and one child that takes the same command.
[Neuron("app.act-probe")]
public sealed class ActProbeScreen : Screen<ActProbeState>
{
    protected override View Render(ActProbeState state) => new("Act probe", new Column([
        new Text($"value={state.Value};by={state.By}"),
        new Field("note", "Note", FieldKind.Text, OnSubmit: Sends(new ActProbeSave())),
        Child<ActProbeChildScreen>("inner"),
    ]));

    [On]
    public Task When(ActProbeSave signal) => Show(new ActProbeState { Value = signal.Value ?? "", By = signal.By ?? "" });
}

[Neuron("test.act-probe-child")]
public sealed class ActProbeChildScreen : Screen<ActProbeState>
{
    protected override View Render(ActProbeState state) => new("Act probe child", new Text($"value={state.Value};by={state.By}"));

    [On]
    public Task When(ActProbeSave signal) => Show(new ActProbeState { Value = signal.Value ?? "", By = signal.By ?? "" });
}

[Module("act-probe")]
public sealed class ActProbeModule : DigitalBrain.Modules.Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class ActProbeFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } = [typeof(ActProbeModule)];
}

// R1: POST /act carries a field's value and the acting person. The edge merges the value into the command's args and refuses
// args that name "by"; the screen that handles the command stamps "by" from the kernel-stamped caller, also after a parent hop.
public sealed class ActCarriesValueAndPersonFacts(ActProbeFixture fixture) : IClassFixture<ActProbeFixture>
{
    private sealed class Proxies(INeuronRuntime runtime) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => runtime.Get<T>(id);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task A_field_submit_over_act_reaches_the_handler_with_the_value_and_the_persons_by(NeuronRuntimeKind kind)
    {
        var view = await ActAndRead(kind, ".", "hello", ".");

        Assert.Contains("value=hello;by=alice-principal", view, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task By_survives_the_hop_through_the_parent_to_its_child(NeuronRuntimeKind kind)
    {
        var view = await ActAndRead(kind, "inner", "to the child", "inner");

        Assert.Contains("value=to the child;by=alice-principal", view, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Args_that_name_by_are_refused_before_the_screen_runs(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        var brain = runtime.Brain.Value;
        using var person = runtime.Edge.Enter(runtime.Edge.Person("alice-principal", "alice-account", runtime.Brain));
        var forged = JsonSerializer.SerializeToElement(new { by = "mallory" });

        var result = await AppUiEndpoints.Act(new($"{brain}/apps/act-probe", ".", "act-probe.save", forged, "hello"),
            new Proxies(runtime), brain, ct, runtime.Edge.Current);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private async Task<string> ActAndRead(NeuronRuntimeKind kind, string target, string value, string readPath)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        var brain = runtime.Brain.Value;
        using var person = runtime.Edge.Enter(runtime.Edge.Person("alice-principal", "alice-account", runtime.Brain));
        var proxies = new Proxies(runtime);
        var screen = runtime.Get<IScreen>(runtime.Id("app.act-probe", "default"));
        _ = await screen.Read();

        var result = await AppUiEndpoints.Act(new($"{brain}/apps/act-probe", target, "act-probe.save", null, value),
            proxies, brain, ct, runtime.Edge.Current);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        await runtime.Idle();
        var shown = readPath == "." ? await screen.Read() : await screen.ReadChild(readPath);
        return Assert.IsType<PublishedView>(shown).View.GetRawText();
    }
}
