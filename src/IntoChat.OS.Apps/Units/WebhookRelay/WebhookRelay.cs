using System.Linq;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace Units.WebhookRelay;

public sealed record RelayEvent(string Topic, string Summary);

[State("relay.webhook")]
public sealed record RelayState(RelayEvent[] Events, string TopicFilter)
{
    public const int MaxEvents = 100;

    public RelayState() : this([], "") { }
}

public static class WebhookRelayUnit
{
    [Synapse("relay.ingest", "relay.webhook")]
    public static RelayState Ingest(SignalEnvelope signal, RelayState state)
    {
        var topic = Read(signal.Payload, "topic")?.Trim();
        var summary = (Read(signal.Payload, "summary") ?? Read(signal.Payload, "text") ?? Read(signal.Payload, "value"))?.Trim();
        if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(summary)) { return state; }
        var next = new RelayEvent(topic, summary);
        return state with { Events = [.. state.Events.TakeLast(RelayState.MaxEvents - 1), next] };
    }

    [Synapse("relay.clear", "relay.webhook")]
    public static RelayState Clear(SignalEnvelope signal, RelayState state)
        => state with { Events = [] };

    [Synapse("relay.webhook")]
    public static View Show(RelayState state)
    {
        var visible = string.IsNullOrEmpty(state.TopicFilter)
            ? state.Events
            : [.. state.Events.Where(e => string.Equals(e.Topic, state.TopicFilter, StringComparison.OrdinalIgnoreCase))];
        return new("Webhook relay", new Column(
        [
            new Heading("Webhook relay", Subtitle: string.IsNullOrEmpty(state.TopicFilter) ? "All topics" : $"Topic: {state.TopicFilter}"),
            new Badge($"{state.Events.Length} captured", Tone: state.Events.Length > 0 ? "accent" : "neutral"),
            new Column([.. visible.Select(ev => (Node)new Card(ev.Topic, ev.Summary, [])
            {
                Style = Mix.Style().Tone(UiTone.Card).Radius(12).Elevation(UiElevation.Subtle),
            })]) { Gap = 8 },
            new Button("clear", "Clear events", new Command(".", "relay.clear"))
            {
                Style = Mix.Style()
                    .Tone(UiTone.Secondary)
                    .Padding(14, 8)
                    .Radius(10)
                    .Motion(UiMotion.Snappy)
                    .OnPress(static s => s.Scale(0.98)),
            },
        ]) { Gap = 12 });
    }

    private static string? Read(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) { return null; }
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            { return property.Value.GetString(); }
        }
        return null;
    }
}
