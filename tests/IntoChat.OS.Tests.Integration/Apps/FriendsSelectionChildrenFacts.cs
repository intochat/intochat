using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Modules.Apps;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

// Friends' section selector is a control.tabStrip child with a controlled selection, on TestOS and on a silo: a select sent through
// the screen reaches the child, the child's SelectionChanged reaches the screen over its wire, and the screen draws the id back as
// the child's selectedId prop.
// Friends' view holds field slots, so the shell module (ui.field) is hosted beside the apps.
public sealed class FriendsRuntimeFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } =
        [.. CatalogFamily.Modules, typeof(ShippedShellAppsModule), typeof(IntoChat.OS.Modules.UI.UiModule), typeof(IntoChat.OS.Modules.Flutter.FlutterModule)];
}

public sealed class FriendsSelectionChildrenFacts(FriendsRuntimeFixture fixture) : IClassFixture<FriendsRuntimeFixture>
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task The_section_round_trips_through_its_tab_strip_child(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("alice-principal", "alice-account", runtime.Brain));
        var screen = runtime.Get<IScreen>(runtime.Id("app.friends", $"select-{kind}"));
        _ = await screen.Read();

        await screen.Dispatch(new Command(FriendsApp.SectionKey, "control.select", JsonSerializer.SerializeToElement(new { id = "feed" })));
        await runtime.Idle();

        var view = Assert.IsType<PublishedView>(await screen.Read()).View.GetRawText();
        Assert.Contains("Viewing feed section.", view, StringComparison.Ordinal);
        Assert.Contains("\"selectedId\":\"feed\"", view, StringComparison.Ordinal);
    }
}
