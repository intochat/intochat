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

    [Fact]
    public async Task ItsKeptScenariosPass()
    {
        var transcript = await ScenarioTranscripts.Run("circleroom", ShippedUnits.Files("circleroom"), null, Ct);
        Assert.False(transcript.Contains('✗', StringComparison.Ordinal), transcript);
        Assert.Equal(3, transcript.Split('\n').Count(line => line.StartsWith('✓')));
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
            new Publish("circleroom.message", JsonElement.Parse("""{"member":"ada","text":"hello"}""")),
        ], Ct);
        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));
        var view = Assert.Single(transcript.Views, published => published.Alias == "circleroom").View.GetRawText();
        Assert.Contains("Members: ada", view, StringComparison.Ordinal);
        Assert.Contains("\"hello\"", view, StringComparison.Ordinal);
        Assert.Contains("circleroom.message", view, StringComparison.Ordinal);
    }
}
