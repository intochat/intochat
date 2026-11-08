using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Flutter;

// A unit owning its view, for the edge's unit route: a bump emit and a copy intent.
[State("ctr")]
public sealed record Counter(int Bumps, string? Value, int Kicks)
{
    public Counter() : this(0, null, 0) { }
}

[Signal("ctr.kick")]
public sealed record Kicked : Signal;

public interface IKicker
{
    Task Kick();
}

[Neuron("kicker")]
public sealed class Kicker : Neuron<KickerState>, IKicker
{
    public Task Kick() => Save(State with { Kicks = State.Kicks + 1 }, new Kicked());
}

public sealed record KickerState
{
    public int Kicks { get; init; }
}

public static class CounterUnit
{
    [Synapse("ctr.kick", "ctr")]
    public static Counter Kicked(SignalEnvelope signal, Counter c) => c with { Kicks = c.Kicks + (signal.Alias.Length > 0 ? 1 : 0) };

    [Synapse("ctr.bump", "ctr")]
    public static Counter Bumped(SignalEnvelope s, Counter c)
        => c with { Bumps = c.Bumps + 1, Value = s.Payload.ValueKind == JsonValueKind.Object && s.Payload.TryGetProperty("value", out var v) ? v.GetString() : null };

    [Synapse("ctr")]
    public static View Show(Counter c) => new("Counter", new Row(
    [
        new Badge(c.Bumps.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new DigitalBrain.UI.Button("bump", "Bump", new Emit("ctr", "ctr.bump")),
        new DigitalBrain.UI.Button("copy", "Copy", Intent: new Intent(IntentKind.Copy, "neuron://owner/ctr")),
        new DigitalBrain.UI.Button("foreign", "Foreign", new Emit("other.alias", "ctr.bump")),
        new ApprovalCardNode("approval", "plan-1", "Approve", "A unit's card", [], Resolve: new Resolve("plan-1")),
    ]));
}
