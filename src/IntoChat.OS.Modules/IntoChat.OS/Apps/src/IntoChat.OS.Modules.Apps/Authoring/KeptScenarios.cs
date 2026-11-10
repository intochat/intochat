using System.Text.Json;

namespace IntoChat.OS.Modules.Apps;

// Keeps a unit's earlier scenarios when the unit is changed: the earlier set and the new set become one.
internal static class KeptScenarios
{
    public static IReadOnlyList<Scenario> Earlier(IReadOnlyDictionary<string, string>? existing)
    {
        if (existing is null || !existing.TryGetValue(UnitFiles.Scenarios, out var json)) { return []; }
        try
        {
            return JsonSerializer.Deserialize<Scenario[]>(json, JsonSerializerOptions.Web) ?? [];
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"scenarios.json of the deployed unit does not read: {error.Message}", error);
        }
    }

    public static IReadOnlyList<Scenario> Merge(IReadOnlyDictionary<string, string>? existing, IReadOnlyList<Scenario> authored, string change)
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(change);
        var list = Earlier(existing).ToList();
        var earlierCount = list.Count;
        foreach (var item in authored)
        {
            if (list.Take(earlierCount).Any(earlier => SameContent(earlier, item))) { continue; }

            var next = item;
            if (list.Any(s => s.Name == next.Name))
            {
                var k = 2;
                while (list.Any(s => s.Name == $"{next.Name} ({k})")) { k++; }
                next = next with { Name = $"{next.Name} ({k})" };
            }

            if (next.Supersedes is { } target)
            {
                var index = list.FindIndex(0, earlierCount, s => s.Name == target);
                if (index < 0)
                {
                    throw new InvalidDataException($"scenario '{next.Name}' supersedes '{target}', which the unit does not have");
                }
                list[index] = list[index] with { Superseded = change.Trim() };
            }

            list.Add(next);
        }
        return list;
    }

    public static IEnumerable<Scenario> Live(IEnumerable<Scenario> scenarios)
        => scenarios.Where(s => s.Superseded is null);

    private static bool SameContent(Scenario a, Scenario b)
        => a.Name == b.Name
            && a.When.SequenceEqual(b.When, StringComparer.Ordinal)
            && a.Then.SequenceEqual(b.Then, StringComparer.Ordinal)
            && GivenText(a.Given) == GivenText(b.Given);

    private static string? GivenText(JsonElement given)
        => given.ValueKind == JsonValueKind.Undefined ? null : JsonSerializer.Serialize(given);
}
