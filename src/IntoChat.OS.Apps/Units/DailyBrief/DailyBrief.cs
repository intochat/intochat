using System.Linq;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace Units.DailyBrief;

[State("brief.daily")]
public sealed record Brief(string[] Priorities, string[] Completed)
{
    public Brief() : this([], []) { }
}

public static class DailyBriefUnit
{
    [Synapse("brief.add", "brief.daily")]
    public static Brief Add(SignalEnvelope signal, Brief brief)
    {
        var item = (Read(signal.Payload, "text") ?? Read(signal.Payload, "value"))?.Trim();
        if (string.IsNullOrEmpty(item) || brief.Priorities.Contains(item, StringComparer.Ordinal))
        {
            return brief;
        }
        return brief with { Priorities = [.. brief.Priorities, item] };
    }

    [Synapse("brief.complete", "brief.daily")]
    public static Brief Complete(SignalEnvelope signal, Brief brief)
    {
        var item = (Read(signal.Payload, "text") ?? Read(signal.Payload, "value"))?.Trim();
        if (string.IsNullOrEmpty(item) || !brief.Priorities.Contains(item, StringComparer.Ordinal))
        {
            return brief;
        }
        return brief with
        {
            Priorities = [.. brief.Priorities.Where(p => p != item)],
            Completed = brief.Completed.Contains(item, StringComparer.Ordinal) ? brief.Completed : [.. brief.Completed, item],
        };
    }

    [Synapse("brief.reset", "brief.daily")]
    public static Brief Reset(SignalEnvelope signal, Brief brief)
        => brief with { Completed = [] };

    [Synapse("brief.daily")]
    public static View Show(Brief brief) => new("Daily brief", new Column(
    [
        new Heading("Daily brief", Subtitle: $"{brief.Priorities.Length} open · {brief.Completed.Length} completed"),
        Ui.StatCard("brief-stats", "Open vs Completed", $"{brief.Priorities.Length} / {brief.Completed.Length}", $"{brief.Completed.Length} done", BadgeTone.Accent),
        new Column([.. brief.Priorities.Select(priority => (Node)new Card(priority, "Open priority", []))]) { Gap = 8 },
        new Field("priority", "brief.daily/priority", FieldKind.Text, "Add a priority", OnSubmit: new Command(".", "brief.add"), AutomationName: "Add a priority"),
    ]) { Gap = 12 });

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
