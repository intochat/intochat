namespace IntoChat.OS.Apps;

[GenerateSerializer, Alias("apps.operation2")]
public sealed record AppOperation([property: Id(0)] string Name, [property: Id(1)] string Description);

[GenerateSerializer, Alias("apps.setting2")]
public sealed record AppSetting([property: Id(0)] string Name, [property: Id(1)] string Description, [property: Id(2)] string Default);

// A named credential slot ("gmail"); the bound connection id is per-brain state, never shared.
[GenerateSerializer, Alias("apps.slot")]
public sealed record AppSlot([property: Id(0)] string Name, [property: Id(1)] string Connector);

public static class AppToolbarKinds
{
    public const string Button = "button";
    public const string Toggle = "toggle";
    public const string Slider = "slider";
    public const string Text = "text";
    public static readonly string[] All = [Button, Toggle, Slider, Text];
}

// One control in the dock: how an operation the app declares is shown, and what input it posts
// (a button "", a toggle "on"/"off", a slider its value as text on release, a text field its text on submit).
[GenerateSerializer, Alias("apps.toolbar-entry")]
public sealed record AppToolbarEntry(
    [property: Id(0)] string Operation,
    [property: Id(1)] string Kind = AppToolbarKinds.Button,
    [property: Id(2)] string? Icon = null,
    [property: Id(3)] string? Label = null,
    [property: Id(4)] double? Min = null,
    [property: Id(5)] double? Max = null);

// What an app needs before it is useful, declared so install asks once and review can gate on it: the semantic types
// it reads (each a grant scope, e.g. "person.birthDate"), the connector kinds its slots bind, and the secret names
// it reads. Declaring a need grants nothing; the install consent does.
[GenerateSerializer, Alias("apps.requirements")]
public sealed record AppRequirements(
    [property: Id(0)] string[]? SemanticTypes = null,
    [property: Id(1)] string[]? Connectors = null,
    [property: Id(2)] string[]? Secrets = null)
{
    // By value, so an origin read back from storage equals the one that was written; null and empty lists are the same need.
    public bool Equals(AppRequirements? other)
        => other is not null && Same(SemanticTypes, other.SemanticTypes) && Same(Connectors, other.Connectors) && Same(Secrets, other.Secrets);

    public override int GetHashCode() => AppConsent.Of(this).GetHashCode(StringComparison.Ordinal);

    private static bool Same(string[]? left, string[]? right) => (left ?? []).SequenceEqual(right ?? [], StringComparer.Ordinal);

    public AppRequirements Validated()
    {
        Check("semantic type", SemanticTypes);
        Check("connector", Connectors);
        Check("secret", Secrets);
        return this;
    }

    private static void Check(string kind, string[]? ids)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var id in ids ?? [])
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !char.IsAsciiLetter(id[0])
                || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            { throw new InvalidDataException($"Required {kind} '{id}' is not an id: a letter, then letters, digits, '.', '-' or '_'."); }
            if (!seen.Add(id)) { throw new InvalidDataException($"Required {kind} '{id}' is listed twice."); }
        }
    }
}

[GenerateSerializer, Alias("apps.manifest2")]
public sealed record AppManifest(
    [property: Id(0)] string Title,
    [property: Id(1)] string Description,
    [property: Id(2)] AppOperation[] Operations,
    [property: Id(3)] AppSetting[] Settings,
    [property: Id(4)] AppSlot[] Slots,
    [property: Id(5)] string? Icon = null,
    [property: Id(6)] AppToolbarEntry[]? Toolbar = null,
    [property: Id(7)] AppRequirements? Requires = null)
{
    // The dock renders only what the app can answer: every entry names a declared operation with one of the four kinds.
    public AppManifest Validated()
    {
        foreach (var entry in Toolbar ?? [])
        {
            if (!Operations.Any(operation => operation.Name == entry.Operation))
            {
                throw new InvalidDataException($"Toolbar entry '{entry.Operation}' is not an operation of this app. It offers: "
                    + string.Join(", ", Operations.Select(operation => operation.Name)) + ".");
            }
            if (!AppToolbarKinds.All.Contains(entry.Kind, StringComparer.Ordinal))
            { throw new InvalidDataException($"Toolbar entry '{entry.Operation}' has kind '{entry.Kind}'; a kind is button, toggle, slider or text."); }
            if (entry.Kind == AppToolbarKinds.Slider && !(entry.Min < entry.Max))
            { throw new InvalidDataException($"Toolbar slider '{entry.Operation}' needs min below max."); }
        }
        if (Requires is { } requires)
        {
            requires.Validated();
            foreach (var slot in Slots)
            {
                if (!(requires.Connectors ?? []).Contains(slot.Connector, StringComparer.Ordinal))
                { throw new InvalidDataException($"Slot '{slot.Name}' binds connector '{slot.Connector}', which Requires does not list."); }
            }
        }
        return this;
    }
}
