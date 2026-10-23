using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Framework.Platform.Entries;

// A variable's value is readable; a secret is write-only and leaves only through Release.
[JsonConverter(typeof(EntryKindJson))]
public enum EntryKind
{
    Variable,
    Secret,
}

internal sealed class EntryKindJson() : JsonStringEnumConverter<EntryKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

// Value is set for variables only. A secret never shows its value; Tail is its last four characters,
// set only for secrets of eight or more characters.
[GenerateSerializer, Alias("brain.entry-summary")]
public sealed record EntrySummary(
    [property: Id(0)] string Name,
    [property: Id(1)] EntryKind Kind,
    [property: Id(2)] bool IsSet,
    [property: Id(3)] string? Value,
    [property: Id(4)] string? Tail,
    [property: Id(5)] DateTimeOffset UpdatedAt);

// One per brain, keyed by the brain scope id. Not a neuron, so no script or app proxy can address it;
// the Authority's owner-only HTTP edge is its only external door.
[Alias("brain.entries"), Orleans.Metadata.DefaultGrainType("brain.entries")]
public interface IBrainEntries : IGrainWithStringKey
{
    Task<EntrySummary[]> List();
    Task Put(string name, EntryKind kind, string value);
    Task Remove(string name);

    // The value itself, for trusted platform code only: the caller must carry the Platform stamp,
    // as IIntegration.Release requires. Never mapped to HTTP.
    Task<string?> Release(CallerContext caller, string name);

    // A counter that moves on every Put and Remove: a cheap change marker for caches of what Release
    // would return. Carries no name or value.
    [Orleans.Concurrency.ReadOnly]
    Task<long> Version();
}

// The AI module's door to a brain's provider key: Authority holds the Platform stamp Release needs,
// so a module never constructs one. Only provider:{id} entries are reachable; null when none is set.
public interface IBrainProviderKeys
{
    Task<string?> Release(string brainId, string providerId, CancellationToken cancellationToken = default);
}

public static partial class BrainEntries
{
    // API keys are secrets named provider:{integrationId}; no other entry may take the prefix.
    public const string ProviderPrefix = "provider:";
    public const int MaxVariableBytes = 4 * 1024;
    public const int MaxSecretBytes = 8 * 1024;

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static string ProviderEntry(string providerId) => ProviderPrefix + providerId;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.:-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
