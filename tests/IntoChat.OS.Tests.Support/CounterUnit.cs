using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Flutter;

// A unit owning its view, for the edge's unit route: a bump emit and a copy intent.
[State("ctr")]
public sealed record Counter(int Bumps, string? Value, int Kicks, string? By = null)
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
        => c with
        {
            Bumps = c.Bumps + 1,
            Value = s.Payload.ValueKind == JsonValueKind.Object && s.Payload.TryGetProperty("value", out var v) ? v.GetString() : null,
            By = s.Payload.ValueKind == JsonValueKind.Object && s.Payload.TryGetProperty("by", out var by) ? by.GetString() : null
        };

    [Synapse("ctr")]
    public static View Show(Counter c) => new("Counter", new Row(
    [
        new Badge(c.Bumps.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new DigitalBrain.UI.Button("bump", "Bump", new Command(".", "ctr.bump")),
        new DigitalBrain.UI.Button("copy", "Copy", Intent: new Intent(IntentKind.Copy, "neuron://owner/ctr")),
        new Tabs([new TabItem("foreign", "foreign", "Foreign", new Emit("other.alias", "ctr.bump"))], "foreign"),
        new Field("valued", "ctr/valued", FieldKind.Text, OnSubmit: new Command(".", "ctr.bump", JsonSerializer.SerializeToElement(new { value = "declared" }))),
        new ApprovalCardNode("approval", "plan-1", "Approve", "A unit's card", [], Resolve: new Resolve("plan-1")),
    ]));
}

// A unit whose view declares the reserved "by": the framework refuses to publish it (ViewDeclaresBy).
[State("forger")]
public sealed record Forger(int Kicks)
{
    public Forger() : this(0) { }
}

public static class ForgerUnit
{
    [Synapse("ctr.kick", "forger")]
    public static Forger Kicked(SignalEnvelope signal, Forger f) => f with { Kicks = f.Kicks + (signal.Alias.Length > 0 ? 1 : 0) };

    [Synapse("forger")]
    public static View Show(Forger f) => new("Forger " + f.Kicks.ToString(System.Globalization.CultureInfo.InvariantCulture), new Row(
    [
        new DigitalBrain.UI.Button("forged", "Forged", new Command(".", "forger.go", JsonSerializer.SerializeToElement(new { by = "mallory" }))),
    ]));
}
