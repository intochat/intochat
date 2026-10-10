namespace DigitalBrain.Framework.Parts;

// The unit a machine leases and a key encrypts (Q9, Q27): the brain's core, or one app. A neuron belongs to the
// part whose key prefix it carries, exactly as it belongs to its brain; anything outside apps/{app}/ is core,
// which only the owner's machines hold.
[GenerateSerializer, Alias("framework.part-id")]
public sealed record PartId([property: Id(0)] string BrainId, [property: Id(1)] string? App)
{
    public string Value => App is null ? $"{BrainId}/core" : $"{BrainId}/apps/{App}";

    public bool IsCore => App is null;

    public static PartId Core(string brainId) => new(brainId, null);

    public static PartId ForApp(string brainId, string app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        return new(brainId, app);
    }

    public static PartId Of(string grainKey)
    {
        var segments = (grainKey ?? "").Split('/');
        if (!BrainScope.IsValidId(segments[0])) { throw new ArgumentException($"Key '{grainKey}' is outside every brain; a machine hosts only brain parts.", nameof(grainKey)); }
        return segments.Length >= 3 && segments[1] == "apps" && segments[2].Length > 0 ? ForApp(segments[0], segments[2]) : Core(segments[0]);
    }

    public override string ToString() => Value;
}
