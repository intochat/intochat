using DigitalBrain;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Flutter.Fields;

// One view field's value, at {brain}/apps/{app}/{field}. A secret kind persists only that a value is set.
[Neuron(UIVocabulary.FieldType)]
internal sealed class FieldNeuron
    : UiNeuron<FieldState>, IField
{
    internal const string SecretSetMarker = "••••••••";

    internal const int MaxInput = 32_000;

    private static bool IsSecret(FieldKind kind) => kind is FieldKind.Password or FieldKind.Secret;

    private static string? Stored(FieldKind kind, string? value) => !IsSecret(kind) || value is null ? value : value.Length == 0 ? "" : SecretSetMarker;

    public Task Configure(FieldKind kind)
    {
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        var current = Snapshot;
        if (current.Kind == kind && current.Version > 0) { return Task.CompletedTask; }
        var value = current.Value;
        if (IsSecret(kind)) { value = Stored(kind, value); }
        // The marker is not a value: a secret that becomes plain starts empty.
        else if (IsSecret(current.Kind) && value is not null) { value = ""; }
        return Write(kind, value);
    }

    public Task Set(string? value)
    {
        if (value is { Length: > MaxInput }) { throw new ArgumentException("A field holds at most 32000 characters.", nameof(value)); }
        return Write(Snapshot.Kind, Stored(Snapshot.Kind, value));
    }

    public Task Input(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Set(value);
    }

    [DigitalBrain.ReadOnlyCall]
    public Task<FieldState> Read()
    {
        var (owner, name) = Address();
        return Task.FromResult(new FieldState { Owner = owner, Name = name, Kind = Snapshot.Kind, Value = Snapshot.Value, Version = Snapshot.Version });
    }

    private Task Write(FieldKind kind, string? value)
    {
        var (owner, name) = Address();
        var version = checked(Snapshot.Version + 1);
        return Save(new FieldState { Owner = owner, Name = name, Kind = kind, Value = value, Version = version }, new FieldChanged(owner, name, version));
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
