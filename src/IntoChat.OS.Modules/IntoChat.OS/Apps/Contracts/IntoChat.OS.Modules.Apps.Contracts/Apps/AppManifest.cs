namespace IntoChat.OS.Modules.Apps;

public sealed record AppOperation(string Name, string Description);

public sealed record AppSetting(string Name, string Description, string Default);

// A named connection slot; the bound connection id is per-brain state, never shared. A slot binds either a capability
// ("storage@1", any connection whose module provides it) or, for a module with no capability yet, one module id
// ("google-sheets"). Exactly one of the two is set.
public sealed record AppSlot(string Name, string Connector = "", string? Capability = null)
{
    // What the slot binds, for display: its capability id, else its module id.
    public string Binds => Capability ?? Connector;
}

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
public sealed record AppToolbarEntry(
    string Operation,
    string Kind = AppToolbarKinds.Button,
    string? Icon = null,
    string? Label = null,
    double? Min = null,
    double? Max = null);

// What an app needs before it is useful, declared so install asks once and review can gate on it: the semantic types
// it reads (each a grant scope, e.g. "person.birthDate"), the module ids its slots bind (only modules that provide no
// capability yet), the secret names it reads, and the capabilities ("storage@1") some connection in the brain must
// provide. Declaring a need grants nothing; the install consent does.
public sealed record AppRequirements(
    string[]? SemanticTypes = null,
    string[]? Connectors = null,
    string[]? Secrets = null,
    string[]? Capabilities = null)
{
    // By value, so an origin read back from storage equals the one that was written; null and empty lists are the same need.
    public bool Equals(AppRequirements? other)
        => other is not null && Same(SemanticTypes, other.SemanticTypes) && Same(Connectors, other.Connectors) && Same(Secrets, other.Secrets)
            && Same(Capabilities, other.Capabilities);

    public override int GetHashCode() => AppConsent.Of(this).GetHashCode(StringComparison.Ordinal);

    private static bool Same(string[]? left, string[]? right) => (left ?? []).SequenceEqual(right ?? [], StringComparer.Ordinal);

    public AppRequirements Validated()
    {
        Check("semantic type", SemanticTypes);
        Check("connector", Connectors);
        Check("secret", Secrets);
        CheckCapabilities(Capabilities);
        return this;
    }

    // A capability id is name@major (DigitalBrain CapabilityId grammar); the module-id grammar above would refuse the '@'.
    private static void CheckCapabilities(string[]? ids)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var id in ids ?? [])
        {
            if (!DigitalBrain.Capabilities.CapabilityId.TryParse(id, out var parsed) || parsed.ToString() != id)
            { throw new InvalidDataException($"Required capability '{id}' is not a capability id: a lowercase name, '@', then a major version such as storage@1."); }
            if (!seen.Add(id)) { throw new InvalidDataException($"Required capability '{id}' is listed twice."); }
        }
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

public sealed record AppManifest(
    string Title,
    string Description,
    AppOperation[] Operations,
    AppSetting[] Settings,
    AppSlot[] Slots,
    string? Icon = null,
    AppToolbarEntry[]? Toolbar = null,
    AppRequirements? Requires = null,
    // A headless app has no program: its build deploys a synapse unit through ISynapseStudio.
    bool Headless = false,
    // The width of the side panel when the app presents one beside its main view; the shell's default (236) when null.
    double? SideWidth = null)
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
        Requires?.Validated();
        foreach (var slot in Slots)
        {
            if (slot.Capability is { } capability)
            {
                if (slot.Connector.Length > 0)
                { throw new InvalidDataException($"Slot '{slot.Name}' binds both capability '{capability}' and module '{slot.Connector}'; bind one."); }
                if (!(Requires?.Capabilities ?? []).Contains(capability, StringComparer.Ordinal))
                { throw new InvalidDataException($"Slot '{slot.Name}' binds capability '{capability}', which Requires.Capabilities does not list."); }
            }
            else if (slot.Connector.Length == 0)
            { throw new InvalidDataException($"Slot '{slot.Name}' binds nothing; give it a capability such as storage@1."); }
            else if (Requires is { } requires && !(requires.Connectors ?? []).Contains(slot.Connector, StringComparer.Ordinal))
            { throw new InvalidDataException($"Slot '{slot.Name}' binds module '{slot.Connector}', which Requires does not list."); }
        }
        return this;
    }
}
