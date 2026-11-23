using System.Reflection;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record AppOperation(string Name, string Description);

public sealed record AppSetting(string Name, string Description, string Default);

// A named connection slot; the bound connection id is per-brain state, never shared. A slot binds either a capability
// ("storage@1", any connection whose module provides it) or, for a module with no capability yet, one module id
// ("google-sheets"). Exactly one of the two is set.
public sealed record AppSlot(string Name, string Connector = "", string? Capability = null, bool Required = true)
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

    public override int GetHashCode()
    {
        var hash = new HashCode();
        AddSorted(ref hash, SemanticTypes);
        AddSorted(ref hash, Connectors);
        AddSorted(ref hash, Secrets);
        AddSorted(ref hash, Capabilities);
        return hash.ToHashCode();
    }

    private static void AddSorted(ref HashCode hash, string[]? values)
    {
        if (values is null) { hash.Add(0); return; }
        var ordered = values.Order(StringComparer.Ordinal).ToArray();
        hash.Add(ordered.Length);
        foreach (var value in ordered) { hash.Add(value, StringComparer.Ordinal); }
    }

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
    double? SideWidth = null,
    // Where the app sits in the desktop's dock (ascending); null keeps it out of the dock.
    int? Dock = null,
    // A studio is an app Ino hands a routed prompt to and lists among its studios.
    bool Studio = false,
    // Optional catalog category for desktop and launcher grouping (e.g. "Workspace", "Studio").
    string? Category = null)
{
    /// <summary>Projects a <see cref="ComponentManifest"/> into an <see cref="AppManifest"/> view.</summary>
    public static AppManifest From(ComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var rawId = manifest.Id.Value ?? string.Empty;
        var fallbackTitle = rawId.StartsWith("app.", StringComparison.Ordinal) ? rawId["app.".Length..] : rawId;
        var title = !string.IsNullOrWhiteSpace(manifest.Face?.Title) ? manifest.Face!.Title : fallbackTitle;
        var description = manifest.Face?.Description ?? string.Empty;
        var operations = manifest.Operations
            .Select(static op => new AppOperation(op.Name, op.Description))
            .ToArray();
        var capabilities = new List<string>();
        var connectors = new List<string>();
        var slots = new AppSlot[manifest.Slots.Length];
        for (var i = 0; i < manifest.Slots.Length; i++)
        {
            var slot = manifest.Slots[i];
            if (slot.Contract.GetCustomAttribute<CapabilityAttribute>(inherit: false) is { } capability)
            {
                var capabilityId = capability.Id.ToString();
                slots[i] = new AppSlot(slot.Name, Connector: "", Capability: capabilityId, Required: slot.Required);
                if (slot.Required && !capabilities.Contains(capabilityId, StringComparer.Ordinal)) { capabilities.Add(capabilityId); }
            }
            else
            {
                var connector = ContractIdOf(slot.Contract);
                slots[i] = new AppSlot(slot.Name, Connector: connector, Required: slot.Required);
                if (slot.Required && !connectors.Contains(connector, StringComparer.Ordinal)) { connectors.Add(connector); }
            }
        }
        var requires = capabilities.Count > 0 || connectors.Count > 0
            ? new AppRequirements(
                Connectors: connectors.Count > 0 ? [.. connectors] : null,
                Capabilities: capabilities.Count > 0 ? [.. capabilities] : null)
            : null;
        return new AppManifest(
            Title: title,
            Description: description,
            Operations: operations,
            Settings: [],
            Slots: slots,
            Icon: manifest.Face?.Icon,
            Requires: requires,
            Category: manifest.Face?.Category);
    }

    private static string ContractIdOf(Type contract)
        => contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
            ?? contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? contract.Name;

    // The dock renders only what the app can answer: every entry names a declared operation with one of the four kinds.
    public AppManifest Validated()
    {
        var title = Title ?? string.Empty;
        var description = Description ?? string.Empty;
        var operations = Operations ?? [];
        var settings = Settings ?? [];
        var slots = Slots ?? [];

        foreach (var operation in operations)
        {
            if (operation is null || string.IsNullOrWhiteSpace(operation.Name))
            { throw new InvalidDataException("Every declared operation must have a name."); }
        }
        foreach (var setting in settings)
        {
            if (setting is null || string.IsNullOrWhiteSpace(setting.Name))
            { throw new InvalidDataException("Every declared setting must have a name."); }
        }
        foreach (var entry in Toolbar ?? [])
        {
            if (entry is null)
            { throw new InvalidDataException("Toolbar entries cannot be null."); }
            if (!operations.Any(operation => operation.Name == entry.Operation))
            {
                throw new InvalidDataException($"Toolbar entry '{entry.Operation}' is not an operation of this app. It offers: "
                    + string.Join(", ", operations.Select(operation => operation.Name)) + ".");
            }
            if (entry.Kind is not (AppToolbarKinds.Button or AppToolbarKinds.Toggle or AppToolbarKinds.Slider or AppToolbarKinds.Text))
            { throw new InvalidDataException($"Toolbar entry '{entry.Operation}' has kind '{entry.Kind}'; a kind is button, toggle, slider or text."); }
            if (entry.Kind == AppToolbarKinds.Slider && !(entry.Min < entry.Max))
            { throw new InvalidDataException($"Toolbar slider '{entry.Operation}' needs min below max."); }
        }
        Requires?.Validated();
        foreach (var slot in slots)
        {
            if (slot is null || string.IsNullOrWhiteSpace(slot.Name))
            { throw new InvalidDataException("Every declared slot must have a name."); }
            var connector = slot.Connector ?? string.Empty;
            if (slot.Capability is { } capability)
            {
                if (connector.Length > 0)
                { throw new InvalidDataException($"Slot '{slot.Name}' binds both capability '{capability}' and module '{connector}'; bind one."); }
                if (slot.Required && !(Requires?.Capabilities ?? []).Contains(capability, StringComparer.Ordinal))
                { throw new InvalidDataException($"Slot '{slot.Name}' binds capability '{capability}', which Requires.Capabilities does not list."); }
            }
            else if (connector.Length == 0)
            { throw new InvalidDataException($"Slot '{slot.Name}' binds nothing; give it a capability such as storage@1."); }
            else if (slot.Required && Requires is { } requires && !(requires.Connectors ?? []).Contains(connector, StringComparer.Ordinal))
            { throw new InvalidDataException($"Slot '{slot.Name}' binds module '{connector}', which Requires does not list."); }
        }
        return ReferenceEquals(title, Title)
            && ReferenceEquals(description, Description)
            && ReferenceEquals(operations, Operations)
            && ReferenceEquals(settings, Settings)
            && ReferenceEquals(slots, Slots)
            ? this
            : this with
            {
                Title = title,
                Description = description,
                Operations = operations,
                Settings = settings,
                Slots = slots,
            };
    }
}
