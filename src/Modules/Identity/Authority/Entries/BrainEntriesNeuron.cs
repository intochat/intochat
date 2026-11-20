using DigitalBrain.Access;
using System.Text;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Authority.Entries;

// A secret keeps only its brain-sealed form and, when long enough, its last four characters; plaintext is never state.
[GenerateSerializer, Alias("brain.entry")]
internal sealed record StoredEntry(
    [property: Id(0)] string Name,
    [property: Id(1)] EntryKind Kind,
    [property: Id(2)] string? Value,
    [property: Id(3)] SealedSecret? Sealed,
    [property: Id(4)] string? Tail,
    [property: Id(5)] DateTimeOffset UpdatedAt);

[GenerateSerializer, Alias("brain.entries-state")]
internal sealed class BrainEntriesState
{
    [Id(0)] public StoredEntry[] Entries { get; set; } = [];
    [Id(1)] public long Version { get; set; }
}

[GrainType("brain.entries")]
internal sealed class BrainEntriesNeuron : Grain, IBrainEntries
{
    internal const string StateName = "entries";

    private readonly ICallerAmbient _callers;

    private readonly IPersistentState<BrainEntriesState> _store;
    private readonly IBrainSecretSealer _sealer;
    private readonly IReadOnlyList<IntegrationSchema> _integrations;
    private readonly TimeProvider _clock;
    private readonly ILogger<BrainEntriesNeuron> _logger;

    public BrainEntriesNeuron(
        [PersistentState(StateName, IntoChatOSNames.PlatformGrainStorage)] IPersistentState<BrainEntriesState> store,
        IBrainSecretSealer sealer, IReadOnlyList<IntegrationSchema> integrations, TimeProvider clock, ILogger<BrainEntriesNeuron> logger,
        ICallerAmbient callers)
    {
        _callers = callers;
        _store = store;
        _sealer = sealer;
        _integrations = integrations;
        _clock = clock;
        _logger = logger;
    }

    public Task<EntrySummary[]> List()
    {
        RefuseAppCalls();
        return Task.FromResult<EntrySummary[]>([.. _store.State.Entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => new EntrySummary(entry.Name, entry.Kind, IsSet: true,
                entry.Kind == EntryKind.Variable ? entry.Value : null,
                entry.Kind == EntryKind.Secret ? entry.Tail : null,
                entry.UpdatedAt))]);
    }

    public async Task Put(string name, EntryKind kind, string value)
    {
        RefuseAppCalls();
        Validate(name, kind, value);
        var brain = this.GetPrimaryKeyString();
        var now = _clock.GetUtcNow();
        var entry = kind == EntryKind.Secret
            ? new StoredEntry(name, kind, null, await _sealer.Seal(brain, name, value), Tail(value), now)
            : new StoredEntry(name, kind, value, null, null, now);
        _store.State.Entries = [.. _store.State.Entries.Where(existing => existing.Name != name), entry];
        _store.State.Version++;
        await _store.WriteStateAsync();
        Audit("put", name, kind);
    }

    public async Task Remove(string name)
    {
        RefuseAppCalls();
        if (!_store.State.Entries.Any(entry => entry.Name == name)) { return; }
        var kind = _store.State.Entries.First(entry => entry.Name == name).Kind;
        _store.State.Entries = [.. _store.State.Entries.Where(entry => entry.Name != name)];
        _store.State.Version++;
        await _store.WriteStateAsync();
        Audit("remove", name, kind);
    }

    public async Task<string?> Release(string name)
    {
        // As IIntegration.Release: only platform code that entered the kernel scope, read from the stamped caller. Users and
        // assistants (authenticated HTTP), apps (app proxy) and the scheduler never qualify.
        if (!CallerRules.IsKernel(_callers.Current))
        { throw new UnauthorizedAccessException("Only trusted platform code can release a brain entry."); }
        if (_store.State.Entries.FirstOrDefault(entry => entry.Name == name) is not { } entry) { return null; }
        Audit("release", name, entry.Kind);
        return entry.Kind == EntryKind.Variable ? entry.Value : await _sealer.Open(this.GetPrimaryKeyString(), entry.Sealed!);
    }

    public Task<long> Version() => Task.FromResult(_store.State.Version);

    // The last four whole characters, and only for secrets of eight or more, so a tail never shows
    // most of a short secret or half a surrogate pair.
    private static string? Tail(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        return runes.Length < 8 ? null : string.Concat(runes[^4..].Select(rune => rune.ToString()));
    }

    // Messages name the entry, never the value.
    private void Validate(string name, EntryKind kind, string value)
    {
        if (!BrainEntries.IsValidName(name)) { throw new ArgumentException("An entry name must match ^[A-Za-z_][A-Za-z0-9_.:-]{0,63}$.", nameof(name)); }
        if (!Enum.IsDefined(kind)) { throw new ArgumentException("Unknown entry kind.", nameof(kind)); }
        if (string.IsNullOrEmpty(value)) { throw new ArgumentException($"Entry '{name}' needs a value.", nameof(value)); }
        var limit = kind == EntryKind.Secret ? BrainEntries.MaxSecretBytes : BrainEntries.MaxVariableBytes;
        if (Encoding.UTF8.GetByteCount(value) > limit) { throw new ArgumentException($"Entry '{name}' exceeds {limit} bytes.", nameof(value)); }
        if (!name.StartsWith(BrainEntries.ProviderPrefix, StringComparison.Ordinal)) { return; }
        if (kind != EntryKind.Secret) { throw new ArgumentException($"'{BrainEntries.ProviderPrefix}' entries are API keys and must be secrets.", nameof(kind)); }
        var provider = name[BrainEntries.ProviderPrefix.Length..];
        if (!_integrations.Any(integration => integration.Id == provider))
        { throw new ArgumentException($"No composed provider is named '{provider}'.", nameof(name)); }
    }

    private void RefuseAppCalls()
    {
        if (_callers.Current is { } ambient && Identity.Grants.GrantRules.IsAppCaller(ambient))
        { throw new UnauthorizedAccessException("Installed apps cannot use brain entries."); }
    }

    private void Audit(string operation, string name, EntryKind kind)
    {
        var actor = _callers.Current is { } current ? current.For?.Principal ?? current.Actor.GetType().Name : "none";
        _logger.LogInformation("Brain entry {Operation} for {Entry} ({Kind}) in {Brain} by {Actor}.",
            operation, name, kind, this.GetPrimaryKeyString(), actor);
    }
}
