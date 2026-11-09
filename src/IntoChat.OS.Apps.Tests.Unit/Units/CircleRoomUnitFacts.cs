using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing.Synapses;
using IntoChat.OS.Modules.Apps.Debugger;

namespace IntoChat.OS.Apps.Tests.Unit.Units;

// The circle room is the first shipped unit source: its kept scenarios pass on fakes, and a join then a message shows both.
public sealed class CircleRoomUnitFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheRoomShipsItsSourceSpecAndScenarios()
        => Assert.Equal(["CircleRoom.cs", "scenarios.json", "spec.md"], ShippedUnits.Files("circleroom").Keys.Order(StringComparer.Ordinal));

    // The kernel names each circle unit by its own spec's first line, not one literal for every alias.
    [Fact]
    public void EachCircleUnitIsTitledByItsSpec()
    {
        Assert.Equal("Circle room", ShippedUnits.Title("circleroom"));
        Assert.All(IntoChat.OS.Modules.Apps.ShippedApps.CircleUnits, alias => Assert.NotEqual(alias, ShippedUnits.Title(alias)));
    }

    [Fact]
    public async Task ItsKeptScenariosPass()
    {
        var transcript = await ScenarioTranscripts.Run("circleroom", ShippedUnits.Files("circleroom"), null, Ct);
        Assert.False(transcript.Contains('✗', StringComparison.Ordinal), transcript);
        Assert.Equal(4, transcript.Split('\n').Count(line => line.StartsWith('✓')));
    }

    [Fact]
    public async Task AJoinThenAMessageShowsBothInTheView()
    {
        var files = ShippedUnits.Files("circleroom");
        var source = new SynapseSource("circleroom", files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal), null);
        var transcript = await UnitScenario.Run(source, new FakeSlots(),
        [
            new Publish("circleroom.join", JsonElement.Parse("""{"member":"ada"}""")),
            new Publish("circleroom.message", JsonElement.Parse("""{"by":"ada","text":"hello"}""")),
        ], Ct);
        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));
        var view = Assert.Single(transcript.Views, published => published.Alias == "circleroom").View.GetRawText();
        Assert.Contains("Members: ada", view, StringComparison.Ordinal);
        Assert.Contains("\"hello\"", view, StringComparison.Ordinal);
        Assert.Contains("circleroom.message", view, StringComparison.Ordinal);
    }

    // The view's own action: its one keyed control that emits is the message field, whose submit carries the text as "value". The
    // payload the framework's Activate journals for it (args plus value) is published as is: with no "by" it is dropped, and the
    // same submit with the author the edge names shows that author, whatever "member" the payload claims.
    [Fact]
    public async Task TheViewsOwnSubmitPostsOnlyUnderTheStampedAuthor()
    {
        var files = ShippedUnits.Files("circleroom");
        var source = new SynapseSource("circleroom", files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal), null);
        var shown = await UnitScenario.Run(source, new FakeSlots(), [new Publish("circleroom.join", JsonElement.Parse("""{"member":"ada"}"""))], Ct);
        var view = Assert.Single(shown.Views, published => published.Alias == "circleroom").View;
        var emitting = Nodes(view).Where(node => node.TryGetProperty("emit", out _) || node.TryGetProperty("onSubmit", out _)).ToArray();
        var field = Assert.Single(emitting);
        Assert.Equal("message", field.GetProperty("key").GetString());
        var submit = field.GetProperty("onSubmit");
        Assert.Equal(("circleroom", "circleroom.message"), (submit.GetProperty("owner").GetString(), submit.GetProperty("signal").GetString()));

        static JsonElement Payload(params (string Name, string Value)[] members)
            => JsonSerializer.SerializeToElement(members.ToDictionary(member => member.Name, member => member.Value));
        var transcript = await UnitScenario.Run(source, new FakeSlots(),
        [
            new Publish("circleroom.message", Payload(("value", "unsigned"))),
            new Publish("circleroom.message", Payload(("member", "intruder"), ("value", "hello"), ("by", "ada"))),
        ], Ct);

        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));
        var after = Assert.Single(transcript.Views.Where(published => published.Alias == "circleroom").TakeLast(1)).View.GetRawText();
        Assert.Contains("\"hello\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("unsigned", after, StringComparison.Ordinal);
        Assert.DoesNotContain("intruder", after, StringComparison.Ordinal);
        Assert.Contains("\"ada\"", after, StringComparison.Ordinal);
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            yield return node;
            foreach (var property in node.EnumerateObject())
            { foreach (var inner in Nodes(property.Value)) { yield return inner; } }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        { foreach (var item in node.EnumerateArray()) { foreach (var inner in Nodes(item)) { yield return inner; } } }
    }
}
