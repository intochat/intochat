using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter.Conversations;

[GenerateSerializer, Alias("ui.chat-index-state")]
[ExportableState]
internal sealed record SessionListState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public ConversationSession[] Entries { get; init; } = [];
    [Id(2)] public bool Imported { get; init; }
    [Id(3)] public string? Current { get; init; }
}

[Neuron(Sessions.Type)]
internal sealed partial class SessionListNeuron(ILogger<SessionListNeuron>? logger = null)
    : UiNeuron<SessionListState>, ISessionList
{
    [DigitalBrain.Slot(Key = IInoConversations.DefaultKey, Required = false)]
    private partial IInoConversations? LegacyConversations { get; }
    private const string LegacyPrefix = "ino/conversations/";
    private bool _importChecked;

    private ILogger Logger => (ILogger?)logger ?? OsConsoleLog.For<SessionListNeuron>();

    [DigitalBrain.Contracts.ReadOnlyCall]
    public async Task<SessionPage> List(bool archived, int skip = 0, int take = Sessions.MaxPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, Sessions.MaxPage);
        await EnsureImportedAsync();
        var reachable = Snapshot.Entries.Where(entry => entry.Archived == archived)
            .OrderByDescending(entry => entry.UpdatedAt).ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .Take(Sessions.MaxListed).ToArray();
        return new SessionPage([.. reachable.Skip(skip).Take(take)], reachable.Length);
    }

    [DigitalBrain.Contracts.ReadOnlyCall]
    public async Task<ConversationSession?> Get(string id)
    {
        ValidId(id);
        await EnsureImportedAsync();
        return Find(id);
    }

    public async Task<ConversationSession> Create(string id, string title)
    {
        ValidId(id);
        var valid = Sessions.ValidTitle(title);
        await EnsureImportedAsync();
        if (Find(id) is { } existing) { return existing; }
        var entry = new ConversationSession(id, valid, Now(), false);
        await Write([.. Snapshot.Entries, entry], Snapshot.Current);
        return entry;
    }

    public async Task<ConversationSession> Open(string id)
    {
        ValidId(id);
        await EnsureImportedAsync();
        if (Find(id) is { } existing)
        {
            if (string.Equals(Snapshot.Current, id, StringComparison.Ordinal) && Snapshot.Imported)
            {
                return existing;
            }
            await Write(Snapshot.Entries, id);
            return existing;
        }
        var entry = new ConversationSession(id, Sessions.NewChat, Now(), false);
        await Write([.. Snapshot.Entries, entry], id);
        return entry;
    }

    [DigitalBrain.Contracts.ReadOnlyCall]
    public async Task<string?> Current()
    {
        await EnsureImportedAsync();
        if (!string.IsNullOrWhiteSpace(Snapshot.Current))
        {
            return Snapshot.Current;
        }
        return Snapshot.Entries
            .Where(static entry => !entry.Archived)
            .OrderByDescending(static entry => entry.UpdatedAt)
            .FirstOrDefault()?.Id
            ?? Snapshot.Entries
                .OrderByDescending(static entry => entry.UpdatedAt)
                .FirstOrDefault()?.Id;
    }

    public async Task Rename(string id, string title)
    {
        var valid = Sessions.ValidTitle(title);
        await EnsureImportedAsync();
        await Update(id, entry => entry.Title == valid ? entry : entry with { Title = valid });
    }

    public async Task Archive(string id)
    {
        await EnsureImportedAsync();
        await Update(id, entry => entry.Archived ? entry : entry with { Archived = true });
    }

    public async Task Unarchive(string id)
    {
        await EnsureImportedAsync();
        await Update(id, entry => entry.Archived ? entry with { Archived = false } : entry);
    }

    public async Task Touch(string id, string? firstMessage)
    {
        ValidId(id);
        await EnsureImportedAsync();
        var at = Now();
        if (Find(id) is not { } entry)
        {
            await Write([.. Snapshot.Entries, new ConversationSession(id, Sessions.TitleFrom(firstMessage), at, false)], Snapshot.Current ?? id);
            return;
        }
        var title = entry.Title is Sessions.NewChat or Sessions.Untitled && firstMessage is not null
            && Sessions.TitleFrom(firstMessage) is var from && from != Sessions.NewChat ? from : entry.Title;
        await Write(Replace(entry with { Title = title, UpdatedAt = at }), Snapshot.Current);
    }

    public async Task SetModel(string id, string? modelChoice)
    {
        if (modelChoice is not null && (string.IsNullOrWhiteSpace(modelChoice) || modelChoice.Length > Sessions.ModelChoiceMax
            || modelChoice.Any(char.IsControl)))
        { throw new ArgumentException($"A model choice is null or 1 to {Sessions.ModelChoiceMax} characters.", nameof(modelChoice)); }
        await EnsureImportedAsync();
        await Update(id, entry => entry.ModelChoice == modelChoice ? entry : entry with { ModelChoice = modelChoice });
    }

    private async Task EnsureImportedAsync()
    {
        if (Snapshot.Imported || _importChecked) { return; }
        _importChecked = true;

        string[] legacyKeys;
        try
        {
            legacyKeys = LegacyConversations is { } legacy ? await legacy.List() : [];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogDebug(error, "Legacy conversation registry could not be read during session list import.");
            return;
        }

        if (legacyKeys is not { Length: > 0 })
        {
            return;
        }

        var existingIds = Snapshot.Entries.Select(static e => e.Id).ToHashSet(StringComparer.Ordinal);
        var importedIds = new List<string>();
        foreach (var raw in legacyKeys)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            var trimmed = raw.Trim();
            var id = trimmed.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? trimmed[LegacyPrefix.Length..] : trimmed;
            if (Sessions.IsId(id) && existingIds.Add(id))
            {
                importedIds.Add(id);
            }
        }

        if (importedIds.Count == 0)
        {
            return;
        }

        // Preserve newest-first order from IInoConversations.List(): index 0 gets the newest timestamp.
        var baseTime = Now();
        var additions = new ConversationSession[importedIds.Count];
        for (var i = 0; i < importedIds.Count; i++)
        {
            additions[i] = new ConversationSession(importedIds[i], Sessions.NewChat, baseTime.AddTicks(-i), false);
        }

        var nextCurrent = Snapshot.Current ?? importedIds[0];
        await Write([.. Snapshot.Entries, .. additions], nextCurrent);
    }

    private async Task Update(string id, Func<ConversationSession, ConversationSession> change)
    {
        ValidId(id);
        var entry = Find(id) ?? throw new KeyNotFoundException($"No chat '{id}' is indexed.");
        var next = change(entry);
        if (next != entry) { await Write(Replace(next), Snapshot.Current); }
    }

    private Task Write(ConversationSession[] entries, string? current)
    {
        var next = new SessionListState
        {
            Revision = Snapshot.Revision + 1,
            Entries = entries,
            Imported = Snapshot.Imported || _importChecked,
            Current = current,
        };
        return Save(next, new SessionsChanged(FullKey, next.Revision));
    }

    private ConversationSession[] Replace(ConversationSession next)
        => [.. Snapshot.Entries.Select(entry => entry.Id == next.Id ? next : entry)];

    private ConversationSession? Find(string id) => Array.Find(Snapshot.Entries, entry => entry.Id == id);

    // Strictly increasing, so entries written in one tick still order newest first.
    private DateTimeOffset Now()
    {
        var now = Context.Time.GetUtcNow();
        var latest = Snapshot.Entries.Length == 0 ? DateTimeOffset.MinValue : Snapshot.Entries.Max(entry => entry.UpdatedAt);
        return now > latest ? now : latest.AddTicks(1);
    }

    private static string ValidId(string id)
        => Sessions.IsId(id) ? id : throw new ArgumentException("A chat id is \"main\" or \"c-\" and 8 or 32 hex digits.", nameof(id));
}
