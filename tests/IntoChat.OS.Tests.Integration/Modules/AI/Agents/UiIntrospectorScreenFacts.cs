using DigitalBrain;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed record IntrospectSiloState
{
    public string Status { get; init; } = "idle";
}

[Neuron("app.introspectsilo")]
public sealed class IntrospectSiloApp : Screen<IntrospectSiloState>
{
    protected override View Render(IntrospectSiloState state)
        => new("Introspect silo", new Column([
            new Button("refresh", "Refresh", new Command(".", "introspectsilo.refresh", null)),
            Child<IntrospectSiloPanel>("panel"),
        ]));
}

[Neuron("test.introspect-silo-panel")]
public sealed class IntrospectSiloPanel : Screen<IntrospectSiloState>
{
    protected override View Render(IntrospectSiloState state)
        => new("Panel", new Column([
            new Text("SILO_PANEL_MARKER"),
            new Button("save", "Save draft", new Command(".", "introspect-silo-panel.save", null)),
        ]));
}

[Module("test-introspect-screens")]
public sealed class IntrospectScreensModule : DigitalBrain.Modules.Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class IntrospectorScreensFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } = [typeof(AIModule), typeof(FlutterModule), typeof(IntrospectScreensModule)];

    protected override void ConfigureSilo(IServiceCollection services)
    {
        services.AddJournalPayloads(typeof(IUiIntrospector).Assembly);
        services.AddJournalPayloads(typeof(AIModule).Assembly);
        services.AddJournalPayloads(typeof(FlutterModule).Assembly);
        services.AddJournalPayloads(typeof(IWorkspace).Assembly);
    }
}

// The introspector reads a screen and its child through the screen directory, the same on TestOS and on a silo.
public sealed class UiIntrospectorScreenFacts(IntrospectorScreensFixture fixture) : IClassFixture<IntrospectorScreensFixture>
{
    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task An_open_screen_and_its_child_are_read_through_the_directory(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(kind, ct);
        using var person = runtime.As(Who.Person);
        var workspace = runtime.Get<IWorkspace>(new NeuronId(runtime.Brain, IWorkspace.Type, "main"));
        await workspace.Show($"{runtime.Brain.Value}/apps/introspectsilo", "op-open-1", 0);
        var introspector = runtime.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(runtime.Brain));

        var screen = await introspector.InspectScreen("introspectsilo");

        Assert.NotNull(screen);
        Assert.Contains(screen.Nodes, node => node is { Key: "refresh", Kind: "button" });
        Assert.Contains(screen.Nodes, node => node is { Key: "panel/save", Label: "Save draft" });
        Assert.Contains("SILO_PANEL_MARKER", screen.ViewJson, StringComparison.Ordinal);

        var report = await introspector.SyncContext();

        Assert.Equal(["introspectsilo"], report.Screens.Select(s => s.AppId));
        Assert.Contains("panel/save", report.ContextSummary, StringComparison.Ordinal);
    }
}
