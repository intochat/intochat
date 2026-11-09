using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Errors;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flutter.Views;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// The view neuron on TestOS: it stores an app's View as its wire JSON and resolves an activation by key to a typed action.
public sealed class ViewNeuronFacts
{
    private static readonly NeuronId Tasks = new(new BrainId("owner"), UIVocabulary.ViewType, "apps/tasks/view");

    private static readonly View Sample = new("Tasks", new Column(
    [
        new DigitalBrain.UI.Text("Two open"),
        new DigitalBrain.UI.Button("cancel", "Cancel", new Emit("tasks", "cancel", JsonSerializer.SerializeToElement(new[] { "f/1", "x:y" }))),
        new DigitalBrain.UI.Button("copy", "Copy", Intent: new Intent(IntentKind.Copy, "neuron://owner/tasks")),
        new DigitalBrain.UI.Button("off", "Off", new Emit("tasks", "off"), Enabled: false),
        new Field("title", "apps/tasks/title", FieldKind.Text, OnSubmit: new Emit("tasks", "rename")),
        new ApprovalCardNode("approval", "plan-1", "Approve", "Detail", [], Resolve: new Resolve("plan-1")),
    ]));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TestOS Started() => TestOS.Local().Register(typeof(FlutterModule).Assembly);

    [Fact]
    public async Task PublishStoresTheViewVerbatim()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        var view = os.Get<IView>(Tasks);

        await view.Publish(1, Sample);
        var published = await view.Read();

        Assert.NotNull(published);
        Assert.Equal(1, published.Generation);
        Assert.Equal(JsonSerializer.Serialize(Sample, RuntimeProtocol.OptionsFor(typeof(View))), published.View.GetRawText());
        Assert.Equal(Sample, published.View.Deserialize<View>(RuntimeProtocol.OptionsFor(typeof(View))));
        Assert.Equal("button", published.View.GetProperty("root").GetProperty("children")[1].GetProperty("kind").GetString());
        Assert.Equal("Copy", published.View.GetProperty("root").GetProperty("children")[2].GetProperty("intent").GetProperty("intentKind").GetString());
    }

    [Fact]
    public async Task AnUnchangedViewKeepsItsGenerationAndAChangedOneAdvancesIt()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        var view = os.Get<IView>(Tasks);

        await view.Publish(1, Sample);
        await view.Publish(1, Sample);
        Assert.Equal(1, (await view.Read())!.Generation);
        await view.Publish(1, Sample with { Title = "Tasks (2)" });
        Assert.Equal(2, (await view.Read())!.Generation);
        // A late publication from an older program generation does not replace the view.
        await view.Publish(2, Sample);
        await view.Publish(1, Sample with { Title = "Old" });
        Assert.Equal("Tasks", (await view.Read())!.View.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ActivateResolvesEmitIntentAndResolveByKey()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        var view = os.Get<IView>(Tasks);
        await view.Publish(1, Sample);

        var cancel = await view.Activate(1, "cancel", null);
        Assert.Equal(ViewActivationKind.Emit, cancel.Kind);
        Assert.Equal(("tasks", "cancel"), (cancel.Emit!.Owner, cancel.Emit.Signal));
        Assert.Equal("[\"f/1\",\"x:y\"]", cancel.Emit.Args!.Value.GetRawText());

        var copy = await view.Activate(1, "copy", null);
        Assert.Equal(new Intent(IntentKind.Copy, "neuron://owner/tasks"), copy.Intent);

        var rename = await view.Activate(1, "title", "Ship docs");
        Assert.Equal(("rename", "Ship docs"), (rename.Emit!.Signal, rename.Value));

        var approval = await view.Activate(1, "approval", "approve");
        Assert.Equal(new Resolve("plan-1"), approval.Resolve);
    }

    [Fact]
    public async Task AStaleGenerationAnUnknownKeyOrADisabledButtonIsRefused()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        var view = os.Get<IView>(Tasks);
        await view.Publish(1, Sample);

        await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(2, "cancel", null));
        await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(1, "missing", null));
        await Assert.ThrowsAsync<StaleActionException>(() => view.Activate(1, "off", null));
    }

    [Fact]
    public async Task AViewOverTheNodeLimitIsRefused()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        var many = new View("Big", new Column([.. Enumerable.Range(0, 600).Select(index => (Node)new DigitalBrain.UI.Text("t" + index))]));

        await Assert.ThrowsAsync<ArgumentException>(() => os.Get<IView>(Tasks).Publish(1, many));
        Node deep = new DigitalBrain.UI.Text("leaf");
        for (var depth = 0; depth < 40; depth++) { deep = new Column([deep]); }
        await Assert.ThrowsAsync<ArgumentException>(() => os.Get<IView>(Tasks).Publish(1, new("Deep", deep)));
    }

    // The store keeps state as JSON through the runtime codec (JournalPayloads.DecodeState; TestOS's journal the same): the
    // Orleans alias is not part of the stored record, so a record of the old shape (UiView under "view") decodes as no view.
    // The writer is the brain's person, its platform or the owning app; the platform of another brain and a call nobody stamped are refused.
    [Theory]
    [InlineData("person", true)]
    [InlineData("platform", true)]
    [InlineData("owning-app", true)]
    [InlineData("platform-of-another-brain", false)]
    [InlineData("other-brain", false)]
    [InlineData("none", false)]
    public async Task PublishByCallerKind(string kind, bool allowed)
    {
        await using var os = Started();
        DigitalBrain.Capabilities.CapabilityCaller? caller = kind switch
        {
            "person" => new("p1", "a1", "owner"),
            "platform" => DigitalBrain.Capabilities.CapabilityCallers.Platform("p1", "a1", "owner"),
            "owning-app" => new("p1", "a1", "owner", AppId: "owner/apps/tasks"),
            "platform-of-another-brain" => DigitalBrain.Capabilities.CapabilityCallers.Platform("p1", "a1", "elsewhere"),
            "other-brain" => new("p1", "a1", "elsewhere"),
            _ => null,
        };
        Task Publish() => os.Get<IView>(Tasks).Publish(1, Sample);
        using (caller is null ? null : DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(caller))
        {
            if (allowed) { await Publish(); }
            else { await Assert.ThrowsAsync<UnauthorizedAccessException>(Publish); }
        }

        Assert.Equal(allowed, await os.Get<IView>(Tasks).Read() is not null);
    }

    [Fact]
    public void AStoredViewStateOfTheOldShapeLoadsAsNoView()
    {
        const string old = """{"view":{"generation":2,"version":3,"root":{"kind":0,"key":"r","children":[]}},"changes":4}""";

        var state = JsonSerializer.Deserialize<ViewState>(old, RuntimeProtocol.OptionsFor(typeof(ViewState)));

        Assert.NotNull(state);
        Assert.Null(state.Json);
        Assert.Equal(0, state.Generation);
        Assert.Equal(4, state.Changes);
    }

    [Fact]
    public void NoUiNodeTypeRemains()
    {
        var contracts = typeof(IView).Assembly.GetTypes().Select(type => type.Name).ToArray();

        Assert.DoesNotContain("UiNode", contracts);
        Assert.DoesNotContain("UiView", contracts);
        Assert.DoesNotContain("UiNodeKind", contracts);
        Assert.DoesNotContain("ViewAction", contracts);
        Assert.DoesNotContain(typeof(IView).GetMethods(), method => method.GetParameters().Any(p => p.ParameterType.Name.StartsWith("Ui", StringComparison.Ordinal)));
    }
}
