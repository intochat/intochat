using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.Flutter.Chat;

[GenerateSerializer, Alias("ui.chat-index-state")]
[ExportableState]
internal sealed record ChatIndexState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public ConversationSession[] Entries { get; init; } = [];
}

[Neuron(Sessions.Type)]
internal sealed class ChatIndexNeuron
    : UiNeuron<ChatIndexState>, ISessionList
{
    [DigitalBrain.Contracts.ReadOnlyCall]
    public Task<SessionPage> List(bool archived, int skip = 0, int take = Sessions.MaxPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, Sessions.MaxPage);
        var reachable = Snapshot.Entries.Where(entry => entry.Archived == archived)
            .OrderByDescending(entry => entry.UpdatedAt).ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .Take(Sessions.MaxListed).ToArray();
        return Task.FromResult(new SessionPage([.. reachable.Skip(skip).Take(take)], reachable.Length));
    }

    [DigitalBrain.Contracts.ReadOnlyCall]
    public Task<ConversationSession?> Get(string id) => Task.FromResult(Find(ValidId(id)));

    public async Task<ConversationSession> Create(string id, string title)
    {
        ValidId(id);
        var valid = Sessions.ValidTitle(title);
        if (Find(id) is { } existing) { return existing; }
        var entry = new ConversationSession(id, valid, Now(), false);
        await Write([.. Snapshot.Entries, entry]);
        return entry;
    }

    public Task<ConversationSession> Open(string id) => Create(id, Sessions.NewChat);

    [DigitalBrain.Contracts.ReadOnlyCall]
    public Task<string?> Current() => Task.FromResult(Snapshot.Entries.OrderByDescending(entry => entry.UpdatedAt).FirstOrDefault()?.Id);

    public Task Rename(string id, string title)
    {
        var valid = Sessions.ValidTitle(title);
        return Update(id, entry => entry.Title == valid ? entry : entry with { Title = valid });
    }

    public Task Archive(string id) => Update(id, entry => entry.Archived ? entry : entry with { Archived = true });

    public Task Unarchive(string id) => Update(id, entry => entry.Archived ? entry with { Archived = false } : entry);

    public async Task Touch(string id, string? firstMessage)
    {
        ValidId(id);
        var at = Now();
        if (Find(id) is not { } entry)
        {
            await Write([.. Snapshot.Entries, new ConversationSession(id, Sessions.TitleFrom(firstMessage), at, false)]);
            return;
        }
        var title = entry.Title is Sessions.NewChat or Sessions.Untitled && firstMessage is not null
            && Sessions.TitleFrom(firstMessage) is var from && from != Sessions.NewChat ? from : entry.Title;
        await Write(Replace(entry with { Title = title, UpdatedAt = at }));
    }

    public Task SetModel(string id, string? modelChoice)
    {
        if (modelChoice is not null && (string.IsNullOrWhiteSpace(modelChoice) || modelChoice.Length > Sessions.ModelChoiceMax
            || modelChoice.Any(char.IsControl)))
        { throw new ArgumentException($"A model choice is null or 1 to {Sessions.ModelChoiceMax} characters.", nameof(modelChoice)); }
        return Update(id, entry => entry.ModelChoice == modelChoice ? entry : entry with { ModelChoice = modelChoice });
    }

    private async Task Update(string id, Func<ConversationSession, ConversationSession> change)
    {
        ValidId(id);
        var entry = Find(id) ?? throw new KeyNotFoundException($"No chat '{id}' is indexed.");
        var next = change(entry);
        if (next != entry) { await Write(Replace(next)); }
    }

    private Task Write(ConversationSession[] entries)
    {
        var next = new ChatIndexState { Revision = Snapshot.Revision + 1, Entries = entries };
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
