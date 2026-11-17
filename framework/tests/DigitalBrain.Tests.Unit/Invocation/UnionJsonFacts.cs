using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Rules;
using DigitalBrain.Tests.Unit.Rules;

namespace DigitalBrain.Tests.Unit.Invocation;

public sealed class UnionJsonFacts
{
    private sealed record Door(string Name, Latch Latch);

    [Fact]
    public void AStoredUnionKeepsItsCase()
    {
        var door = new Door("front", new Locked(4, "1234"));

        var json = JsonSerializer.Serialize(door, RuntimeProtocol.Json);
        var back = JsonSerializer.Deserialize<Door>(json, RuntimeProtocol.Json)!;

        Assert.Contains("\"$case\":\"Locked\"", json, StringComparison.Ordinal);
        Assert.Equal(door, back);
        Assert.IsType<Locked>(back.Latch.Value);
    }

    [Fact]
    public void EveryCaseRoundTrips()
    {
        foreach (Latch value in new Latch[] { new Unlocked(1), new Locked(1, "9") })
        {
            var back = JsonSerializer.Deserialize<Latch>(JsonSerializer.Serialize(value, RuntimeProtocol.Json), RuntimeProtocol.Json);
            Assert.Equal(value.Value, back.Value);
        }
    }

    [Fact]
    public void AnUnknownCaseIsRefusedOnRead() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Latch>("""{"$case":"Jammed","opens":1}""", RuntimeProtocol.Json));

    [Fact]
    public void AMissingCaseIsRefusedOnRead() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Latch>("""{"opens":1}""", RuntimeProtocol.Json));

    [Fact]
    public void ADefaultUnionIsRefusedOnWrite() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(default(Latch), RuntimeProtocol.Json));

    [Fact]
    public void AnOutcomeIsNotStorable() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new Locked(0, "1").Open("2"), RuntimeProtocol.Json));
}
