using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Authoring;

/// <summary>
/// Domain specification for an authored unit.
/// </summary>
public sealed record AuthoringSpec(
    string Alias,
    string Title,
    string Spec,
    IReadOnlyDictionary<string, string> State,
    IReadOnlyList<AuthoringSignal> Signals,
    AuthoringNeeds Needs,
    IReadOnlyList<AuthoringScenario> Scenarios)
{
    public static void ValidateAlias(string alias)
    {
        var parts = (alias ?? "").Split('.');
        if (parts.Length < 2 || parts.Any(part => part.Length == 0 || !char.IsAsciiLetterLower(part[0]) || part.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))))
        {
            throw new InvalidDataException($"'{alias}' is not a valid unit alias: lowercase dotted words such as \"x.watch\".");
        }
    }

    public void Validate()
    {
        ValidateAlias(Alias);
        if (string.IsNullOrWhiteSpace(Title))
        {
            throw new InvalidDataException("The authoring spec has no title.");
        }
        if (string.IsNullOrWhiteSpace(Spec))
        {
            throw new InvalidDataException("The authoring spec has no spec markdown.");
        }
        if (State is null || Needs is null || Signals is null || Scenarios is null)
        {
            throw new InvalidDataException("The authoring spec requires state, signals, needs, and scenarios.");
        }
        if (Scenarios.Count == 0)
        {
            throw new InvalidDataException("The authoring spec requires at least one scenario.");
        }
        foreach (var signal in Signals)
        {
            ValidateAlias(signal.Alias);
        }
        foreach (var slot in Needs.Slots ?? [])
        {
            if (string.IsNullOrEmpty(slot.Name) || slot.Name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)))
            {
                throw new InvalidDataException($"'{slot.Name}' is not a valid slot name: lowercase letters and digits.");
            }
            if (slot.Capability is not ("http" or "http@1" or "tool" or "tool@1" or "storage" or "storage@1"))
            {
                throw new InvalidDataException($"Slot '{slot.Name}' has capability '{slot.Capability}'; a slot is \"http\", \"tool\", or \"storage\".");
            }
        }
    }
}

public sealed record AuthoringSignal(string Alias, IReadOnlyDictionary<string, string> Fields);

public sealed record AuthoringNeeds(IReadOnlyList<AuthoringSlotNeed> Slots, string? Schedule, bool View);

public sealed record AuthoringSlotNeed(string Name, string Capability, IReadOnlyList<string> Hosts);

public sealed record AuthoringScenario(
    string Name,
    string? GivenJson,
    IReadOnlyList<string> When,
    IReadOnlyList<string> Then,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Supersedes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Superseded = null);
