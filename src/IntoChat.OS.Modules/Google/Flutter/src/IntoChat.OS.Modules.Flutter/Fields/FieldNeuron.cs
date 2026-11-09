using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Flutter.Fields;

// One view field's value, at {brain}/apps/{app}/{field}. Only the owning app, its brain's person or the platform configures, sets
// or takes it; input comes only from the brain's person (through the edge) or its platform. A call with no stamped caller is refused. A secret kind persists and journals only that a value is set: the raw value lives in
// this activation's memory until the owner takes it once, and is gone when the neuron deactivates.
[Neuron(UIVocabulary.FieldType)]
internal sealed class FieldNeuron
    : UiNeuron<FieldState>, IField
{
    internal const string SecretSetMarker = "••••••••";

    internal const int MaxInput = 32_000;

    private string? _secret;

    private static bool IsSecret(FieldKind kind) => kind is FieldKind.Password or FieldKind.Secret;

    private static string? Stored(FieldKind kind, string? value) => !IsSecret(kind) || value is null ? value : value.Length == 0 ? "" : SecretSetMarker;

    public Task Configure(FieldKind kind)
    {
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        RequireOwner();
        var current = Snapshot;
        if (current.Kind == kind && current.Version > 0) { return Task.CompletedTask; }
        var value = current.Value;
        if (IsSecret(kind)) { value = Stored(kind, value); }
        // The marker is not a value: a secret that becomes plain starts empty.
        else if (IsSecret(current.Kind)) { _secret = null; value = value is null ? null : ""; }
        // A field's first configuration only records its kind: it journals FieldConfigured, not FieldChanged.
        return current.Version == 0 ? Write(kind, value, journal: false) : Write(kind, value);
    }

    public Task Set(string? value)
    {
        RequireOwner();
        return Assign(value);
    }

    public Task Input(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // A person's typed text, through the edge, or the platform: never an app (it sets its own fields with Set), another brain,
        // or a call no edge stamped.
        var caller = RuntimeCallerContext.TryGet(out var stamped) ? stamped : throw Unstamped();
        if (!BrainKey.BelongsTo(FullKey, caller.BrainId) || caller.Kind is not (CallerKind.User or CallerKind.Platform))
        { throw new UnauthorizedAccessException("Only the brain's person types into this field."); }
        if (Snapshot.Version == 0) { throw new InvalidOperationException("This field is not configured yet: its view has not been published."); }
        return Assign(value);
    }

    public Task<string?> TakeSecret()
    {
        RequireOwner();
        var taken = _secret;
        _secret = null;
        return Task.FromResult(taken);
    }

    [DigitalBrain.ReadOnlyCall]
    public Task<FieldState> Read()
    {
        var (owner, name) = Address();
        return Task.FromResult(new FieldState { Owner = owner, Name = name, Kind = Snapshot.Kind, Value = Snapshot.Value, Version = Snapshot.Version });
    }

    private Task Assign(string? value)
    {
        if (value is { Length: > MaxInput }) { throw new ArgumentException("A field holds at most 32000 characters.", nameof(value)); }
        var kind = Snapshot.Kind;
        if (IsSecret(kind)) { _secret = value is { Length: > 0 } ? value : null; }
        return Write(kind, Stored(kind, value));
    }

    private Task Write(FieldKind kind, string? value, bool journal = true)
    {
        var (owner, name) = Address();
        var version = checked(Snapshot.Version + 1);
        var next = new FieldState { Owner = owner, Name = name, Kind = kind, Value = value, Version = version };
        return Save(next, journal ? new FieldChanged(owner, name, version) : new FieldConfigured(owner, name, kind));
    }

    private static UnauthorizedAccessException Unstamped() => new("No caller was stamped on this call; a field answers only a stamped one.");

    // The owning app ({brain}/apps/{app}/…), its brain's person, or its brain's platform; as the view neuron's writer rule. A call with no
    // stamped caller is refused, never taken for the platform.
    private void RequireOwner()
    {
        var caller = RuntimeCallerContext.TryGet(out var stamped) ? stamped : throw Unstamped();
        if (!IntoChat.OS.Modules.Flutter.Views.ViewNeuron.Writes(caller, FullKey)) { throw new UnauthorizedAccessException("Only the owning brain and app may change this field."); }
    }

    // {brain}/apps/{app}/{field…}: the owner is the app, the name the rest; any other key splits at its last segment.
    private (string Owner, string Name) Address()
    {
        var key = FullKey;
        var parts = key.Split('/');
        if (parts.Length > 3 && parts[1] == "apps") { return (string.Join('/', parts[..3]), string.Join('/', parts[3..])); }
        var last = key.LastIndexOf('/');
        return last < 0 ? ("", key) : (key[..last], key[(last + 1)..]);
    }
}
