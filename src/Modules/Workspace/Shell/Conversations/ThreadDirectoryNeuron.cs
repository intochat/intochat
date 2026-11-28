using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Notifications;
using IntoChat.Sdk;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter.Conversations;

internal sealed record ConversationPointer(BrainId Brain, string Conversation, string Title, string By);

[GenerateSerializer, Alias("ui.chat-index-state")]
[ExportableState]
internal sealed record ThreadDirectoryState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public ThreadEntry[] Entries { get; init; } = [];
    [Id(2)] public bool Imported { get; init; }
    [Id(3)] public string? Current { get; init; }
    [Id(4)] public ConversationPointer[] Conversations { get; init; } = [];
}

// Unified directory of all threads a person owns or sits in (DirectAssistant, Squad, DirectPerson, SharedRoom),
// replacing SessionListNeuron + RoomsNeuron + InoConversationsNeuron.
[Neuron(Threads.Type)]
internal sealed class ThreadDirectoryNeuron(
    IServiceProvider? services = null,
    ILogger<ThreadDirectoryNeuron>? logger = null)
    : UiNeuron<ThreadDirectoryState>, IThreadDirectory
{
    private ILogger Logger => (ILogger?)logger ?? OsConsoleLog.For<ThreadDirectoryNeuron>();

    public Task<ThreadPage> List(bool archived = false, int skip = 0, int take = Threads.MaxPage, ThreadKind? kind = null)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, Threads.MaxPage);

        var reachable = Snapshot.Entries
            .Where(entry => entry.Archived == archived && (kind is null || entry.Kind == kind.Value))
            .OrderByDescending(static entry => entry.UpdatedAt)
            .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
            .Take(Threads.MaxListed)
            .ToArray();
        return Task.FromResult(new ThreadPage([.. reachable.Skip(skip).Take(take)], reachable.Length));
    }

    public Task<ThreadShelf> Shelf()
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        var active = Snapshot.Entries
            .Where(static entry => !entry.Archived)
            .OrderByDescending(static entry => entry.UpdatedAt)
            .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
            .Take(Threads.MaxListed)
            .ToArray();

        var favourites = active.Where(static e => e.IsFavourite).ToArray();
        var pinned = active.Where(static e => e.IsPinned).ToArray();
        var folders = active
            .Where(static e => !string.IsNullOrWhiteSpace(e.Folder))
            .Select(static e => e.Folder!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var totalUnread = active.Sum(static e => e.UnreadCount);

        return Task.FromResult(new ThreadShelf(favourites, pinned, folders, active, totalUnread));
    }

    public Task<ThreadEntry?> Get(string id)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ValidId(id);
        return Task.FromResult(Find(id));
    }

    public async Task<ThreadEntry> Create(
        string id,
        string title,
        ThreadTrustPosture posture = ThreadTrustPosture.OwnerTrusted,
        ThreadKind kind = ThreadKind.DirectAssistant)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ValidId(id);
        var validTitle = Threads.ValidTitle(title);
        if (Find(id) is { } existing) { return existing; }

        var threadId = new NeuronId(Id.Brain, IConversation.Type, ThreadKeyForLocal(id));
        var entry = new ThreadEntry(
            Id: id,
            Title: validTitle,
            UpdatedAt: Now(),
            Archived: false,
            Thread: threadId,
            TrustPosture: posture,
            Kind: kind,
            IsHost: true);
        await Write([.. Snapshot.Entries, entry], Snapshot.Current, threadId, "created");
        return entry;
    }

    public async Task<NeuronId> CreateShared(string title, ThreadKind kind = ThreadKind.SharedRoom, string? threadKey = null)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        var cleanTitle = Threads.ValidTitle(title);
        if (kind is not (ThreadKind.DirectPerson or ThreadKind.SharedRoom))
        {
            throw new ArgumentException("Shared threads must use ThreadKind.DirectPerson or ThreadKind.SharedRoom.", nameof(kind));
        }

        var key = string.IsNullOrWhiteSpace(threadKey)
            ? $"{(kind == ThreadKind.DirectPerson ? "dm" : "room")}-{Guid.NewGuid():N}"
            : threadKey.Trim();
        var fullKey = key.StartsWith("conversations/", StringComparison.Ordinal) ? key : "conversations/" + key;
        var threadId = new NeuronId(Id.Brain, IConversation.Type, fullKey);
        var entryId = Threads.EntryIdFor(Id.Brain, threadId);

        var existing = FindByThreadOrId(threadId, entryId);
        if (existing is not null && existing.TrustPosture != ThreadTrustPosture.CrossBrainShared)
        {
            throw new InvalidOperationException(
                $"Thread '{entryId}' already exists with immutable TrustPosture '{existing.TrustPosture}' and cannot be converted to '{ThreadTrustPosture.CrossBrainShared}'.");
        }
        var entry = existing is not null
            ? existing with { Title = cleanTitle, Kind = kind, TrustPosture = ThreadTrustPosture.CrossBrainShared, UpdatedAt = Now(), Archived = false }
            : new ThreadEntry(
                Id: entryId,
                Title: cleanTitle,
                UpdatedAt: Now(),
                Archived: false,
                Thread: threadId,
                TrustPosture: ThreadTrustPosture.CrossBrainShared,
                Kind: kind,
                IsHost: true);
        var next = Snapshot.Entries.Where(e => e.Id != entry.Id).Append(entry).ToArray();
        await Write(next, Snapshot.Current, threadId, "created");
        return threadId;
    }

    public async Task<ThreadEntry> Open(string id)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ValidId(id);
        if (Find(id) is { } existing)
        {
            if (string.Equals(Snapshot.Current, id, StringComparison.Ordinal))
            {
                return existing;
            }
            await Write(Snapshot.Entries, id);
            return existing;
        }

        var threadId = new NeuronId(Id.Brain, IConversation.Type, ThreadKeyForLocal(id));
        var entry = new ThreadEntry(id, Threads.NewChat, Now(), false, Thread: threadId, IsHost: true);
        await Write([.. Snapshot.Entries, entry], id);
        return entry;
    }

    public Task<string?> Current()
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        if (!string.IsNullOrWhiteSpace(Snapshot.Current))
        {
            return Task.FromResult<string?>(Snapshot.Current);
        }

        var fallback = Snapshot.Entries
            .Where(static entry => !entry.Archived && entry.TrustPosture == ThreadTrustPosture.OwnerTrusted)
            .OrderByDescending(static entry => entry.UpdatedAt)
            .FirstOrDefault()?.Id
            ?? Snapshot.Entries
                .Where(static entry => !entry.Archived)
                .OrderByDescending(static entry => entry.UpdatedAt)
                .FirstOrDefault()?.Id
            ?? Snapshot.Entries
                .OrderByDescending(static entry => entry.UpdatedAt)
                .FirstOrDefault()?.Id;
        return Task.FromResult(fallback);
    }

    public async Task Rename(string id, string title)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        var valid = Threads.ValidTitle(title);
        await Update(id, entry => entry.Title == valid ? entry : entry with { Title = valid });
    }

    public async Task Archive(string id)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        await Update(id, entry => entry.Archived ? entry : entry with { Archived = true });
    }

    public async Task Unarchive(string id)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        await Update(id, entry => entry.Archived ? entry with { Archived = false } : entry);
    }

    public async Task Touch(string id, string? firstMessage)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ValidId(id);
        var at = Now();
        if (Find(id) is not { } entry)
        {
            var threadId = new NeuronId(Id.Brain, IConversation.Type, ThreadKeyForLocal(id));
            var created = new ThreadEntry(id, Threads.TitleFrom(firstMessage), at, false, Thread: threadId, IsHost: true);
            await Write([.. Snapshot.Entries, created], Snapshot.Current ?? id);
            return;
        }

        var title = entry.Title is Threads.NewChat or Threads.Untitled && firstMessage is not null
            && Threads.TitleFrom(firstMessage) is var from && from != Threads.NewChat
                ? from
                : entry.Title;
        await Write(Replace(entry with { Title = title, UpdatedAt = at }), Snapshot.Current);
    }

    public async Task SetModel(string id, string? modelChoice)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        if (modelChoice is not null && (string.IsNullOrWhiteSpace(modelChoice) || modelChoice.Length > Threads.ModelChoiceMax
            || modelChoice.Any(char.IsControl)))
        {
            throw new ArgumentException($"A model choice is null or 1 to {Threads.ModelChoiceMax} characters.", nameof(modelChoice));
        }

        await Update(id, entry => entry.ModelChoice == modelChoice ? entry : entry with { ModelChoice = modelChoice });
    }

    public async Task SetFavourite(string id, bool favourite)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        await Update(id, entry => entry.IsFavourite == favourite ? entry : entry with { IsFavourite = favourite });
    }

    public async Task SetPinned(string id, bool pinned)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        await Update(id, entry => entry.IsPinned == pinned ? entry : entry with { IsPinned = pinned });
    }

    public async Task SetFolder(string id, string? folder)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        var validFolder = Threads.ValidFolder(folder);
        await Update(id, entry => entry.Folder == validFolder ? entry : entry with { Folder = validFolder });
    }

    public async Task MarkRead(string id, long revision)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        await Update(id, entry =>
        {
            var nextRead = Math.Max(entry.LastReadRevision, revision);
            var unread = ComputeUnread(entry.LastRevision, nextRead);
            return entry.LastReadRevision == nextRead && entry.UnreadCount == unread
                ? entry
                : entry with { LastReadRevision = nextRead, UnreadCount = unread };
        });
    }

    public async Task Leave(NeuronId thread)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        var entryId = Threads.EntryIdFor(Id.Brain, thread);
        if (FindByThreadOrId(thread, entryId) is not { } existing)
        {
            return;
        }

        var next = Snapshot.Entries.Where(e => e.Id != existing.Id).ToArray();
        var nextCurrent = string.Equals(Snapshot.Current, existing.Id, StringComparison.Ordinal) ? null : Snapshot.Current;
        await Write(next, nextCurrent, thread, "left");
    }

    public async Task Invited(NeuronId thread, string? title = null, ThreadKind? kind = null)
    {
        await RequireNoticeOf(thread);
        var entryId = Threads.EntryIdFor(Id.Brain, thread);
        var existing = FindByThreadOrId(thread, entryId);
        var cleanTitle = !string.IsNullOrWhiteSpace(title) ? title.Trim() : existing?.Title ?? entryId;
        var resolvedKind = kind ?? existing?.Kind ?? ThreadKind.SharedRoom;
        var previousUnread = existing?.UnreadCount ?? 0;
        var inviteUnread = Math.Max(previousUnread, 1);
        var entry = existing is not null
            ? existing with
            {
                Title = cleanTitle,
                Kind = resolvedKind,
                TrustPosture = ThreadTrustPosture.CrossBrainShared,
                Thread = thread,
                UpdatedAt = Now(),
                Archived = false,
                UnreadCount = inviteUnread,
                IsHost = thread.Brain == Id.Brain,
            }
            : new ThreadEntry(
                Id: entryId,
                Title: cleanTitle,
                UpdatedAt: Now(),
                Archived: false,
                Thread: thread,
                TrustPosture: ThreadTrustPosture.CrossBrainShared,
                Kind: resolvedKind,
                UnreadCount: inviteUnread,
                IsHost: thread.Brain == Id.Brain);

        var next = Snapshot.Entries.Where(e => e.Id != entry.Id).Append(entry).ToArray();
        using var scope = OwnModuleIfForeign();
        await Write(next, Snapshot.Current, thread, "invited");
        await EmitNotificationAsync(
            title: resolvedKind == ThreadKind.DirectPerson ? $"New direct message: {cleanTitle}" : $"Room invite: {cleanTitle}",
            body: $"You were invited to '{cleanTitle}'.",
            category: "messenger.invite",
            actionUrl: $"os://app/ino?conversation={entry.Id}",
            dedupeKey: $"thread-invite:{entry.Id}");
    }

    public async Task Changed(NeuronId thread, long revision)
    {
        await RequireNoticeOf(thread);
        var entryId = Threads.EntryIdFor(Id.Brain, thread);
        var existing = FindByThreadOrId(thread, entryId);
        var previousUnread = existing?.UnreadCount ?? 0;
        var nextRevision = Math.Max(existing?.LastRevision ?? 0, revision);
        var lastRead = existing?.LastReadRevision ?? 0;
        var unread = ComputeUnread(nextRevision, lastRead);

        var entry = existing is not null
            ? existing with
            {
                Thread = thread,
                LastRevision = nextRevision,
                UnreadCount = unread,
                UpdatedAt = Now(),
            }
            : new ThreadEntry(
                Id: entryId,
                Title: entryId,
                UpdatedAt: Now(),
                Archived: false,
                Thread: thread,
                TrustPosture: ThreadTrustPosture.CrossBrainShared,
                Kind: ThreadKind.SharedRoom,
                LastRevision: nextRevision,
                LastReadRevision: 0,
                UnreadCount: unread,
                IsHost: thread.Brain == Id.Brain);

        var next = Snapshot.Entries.Where(e => e.Id != entry.Id).Append(entry).ToArray();
        var nextState = Snapshot with
        {
            Revision = Snapshot.Revision + 1,
            Entries = next,
        };
        using var scope = OwnModuleIfForeign();
        await Save(nextState, new ThreadRevisionChanged(thread, nextRevision));

        if (unread > previousUnread && !string.Equals(Snapshot.Current, entry.Id, StringComparison.Ordinal))
        {
            var isDm = entry.Kind == ThreadKind.DirectPerson;
            var label = string.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title;
            await EmitNotificationAsync(
                title: isDm ? $"New message from {label}" : $"New activity in {label}",
                body: $"{unread} unread update(s) in '{label}'.",
                category: isDm ? "messenger.dm" : "messenger.mention",
                actionUrl: $"os://app/ino?conversation={entry.Id}",
                dedupeKey: $"thread-rev:{entry.Id}:{nextRevision}");
        }
    }

    private async Task EmitNotificationAsync(
        string title,
        string body,
        string category,
        string actionUrl,
        string dedupeKey)
    {
        try
        {
            var inbox = Context.Get<INotificationsInbox>(INotificationsInbox.Key(Id.Brain.Value));
            await inbox.SendAsync(new NotificationRequest(
                Title: title,
                Body: body,
                Priority: NotificationPriority.Normal,
                Category: category,
                ActionUrl: actionUrl,
                DedupeKey: dedupeKey));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogSwallowed(error, "messenger notification inbox unavailable");
        }
    }

    public Task<long> LastRevision(NeuronId thread)
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        var entryId = Threads.EntryIdFor(Id.Brain, thread);
        var existing = FindByThreadOrId(thread, entryId);
        return Task.FromResult(existing?.LastRevision ?? 0);
    }

    public async Task InvitedToConversation(ConversationInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        await RequireInvitationTo(invite);
        var pointer = Pointer(invite);
        var others = (Snapshot.Conversations ?? []).Where(existing => !Same(existing, pointer));
        var nextState = Snapshot with
        {
            Revision = Snapshot.Revision + 1,
            Conversations = [.. others, pointer],
        };
        using var scope = OwnModuleIfForeign();
        await Save(nextState, new ThreadDirectoryChanged(FullKey, nextState.Revision));
    }

    public async Task AdoptConversations(ConversationInvite[] invites)
    {
        ArgumentNullException.ThrowIfNull(invites);
        if (!FromOwnRoster()) { throw FriendRelay.Refused(); }
        var held = Snapshot.Conversations ?? [];
        var added = new List<ConversationPointer>();
        foreach (var invite in invites)
        {
            if (invite is null) { continue; }
            var pointer = Pointer(invite);
            if (!held.Any(existing => Same(existing, pointer)) && !added.Any(existing => Same(existing, pointer)))
            {
                added.Add(pointer);
            }
        }
        if (added.Count == 0) { return; }
        var nextState = Snapshot with
        {
            Revision = Snapshot.Revision + 1,
            Conversations = [.. held, .. added],
        };
        await Save(nextState, new ThreadDirectoryChanged(FullKey, nextState.Revision));
    }

    public Task<ConversationInvite[]> Conversations()
    {
        _ = RequireSameBrain(FriendRelay.Refused);
        return Task.FromResult((Snapshot.Conversations ?? [])
            .Select(static pointer => new ConversationInvite(pointer.Brain, new ConversationId { Value = pointer.Conversation }, pointer.Title, new AccountId(pointer.By)))
            .ToArray());
    }

    private async Task Update(string id, Func<ThreadEntry, ThreadEntry> change)
    {
        ValidId(id);
        var entry = Find(id) ?? throw new KeyNotFoundException($"No thread '{id}' is indexed.");
        var next = change(entry);
        if (next != entry)
        {
            await Write(Replace(next), Snapshot.Current);
        }
    }

    private Task Write(ThreadEntry[] entries, string? current, NeuronId? thread = null, string? action = null)
    {
        var next = Snapshot with
        {
            Revision = Snapshot.Revision + 1,
            Entries = entries,
            Imported = true,
            Current = current,
        };
        return Save(next, new ThreadDirectoryChanged(FullKey, next.Revision, thread, action));
    }

    private ThreadEntry[] Replace(ThreadEntry next)
        => [.. Snapshot.Entries.Select(entry => entry.Id == next.Id ? next : entry)];

    private ThreadEntry? Find(string id)
        => Array.Find(Snapshot.Entries, entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

    private ThreadEntry? FindByThreadOrId(NeuronId thread, string entryId)
        => Array.Find(Snapshot.Entries, entry =>
            (entry.Thread is { } t && t == thread)
            || string.Equals(entry.Id, entryId, StringComparison.Ordinal));

    private DateTimeOffset Now()
    {
        var now = Context.Time.GetUtcNow();
        var latest = Snapshot.Entries.Length == 0 ? DateTimeOffset.MinValue : Snapshot.Entries.Max(static entry => entry.UpdatedAt);
        return now > latest ? now : latest.AddTicks(1);
    }

    private static int ComputeUnread(long lastRevision, long lastReadRevision)
        => lastRevision <= lastReadRevision ? 0 : (int)Math.Min(int.MaxValue, lastRevision - lastReadRevision);

    private static string ThreadKeyForLocal(string id)
        => id.StartsWith("conversations/", StringComparison.Ordinal) ? id : "conversations/" + id;

    private static string ValidId(string id)
        => Threads.IsValidId(id) ? id : throw new ArgumentException("Thread id must be 1 to 256 non-control characters.", nameof(id));

    private static ConversationPointer Pointer(ConversationInvite invite)
        => new(invite.Brain, invite.Conversation.Value ?? "", invite.Title ?? "", invite.By.Value ?? "");

    private static bool Same(ConversationPointer one, ConversationPointer other)
        => one.Brain == other.Brain && string.Equals(one.Conversation, other.Conversation, StringComparison.Ordinal);

    private bool FromOwnRoster()
        => IsSameBrainCaller() && Context.Caller is { Inside: true, Via: { } via } && via == FriendsDefaults.Id(Id.Brain.Value);

    private IDisposable? OwnModuleIfForeign()
    {
        if (Context.Caller is { Brain: var brain } && brain == Id.Brain)
        {
            return null;
        }
        var edge = services?.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        return edge?.Enter(edge.Module(Threads.Type, Id.Brain));
    }

    private async Task<bool> IsFriendAsync(AccountId account)
    {
        if (services?.GetService(typeof(IFriendCheck)) is not IFriendCheck roster) { return false; }
        var edge = services.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        using var module = edge?.Enter(edge.Module(Threads.Type, Id.Brain));
        return await roster.IsFriend(Id.Brain, account.Value);
    }

    private async ValueTask<string?> ResolvePeerAccountAsync(BrainId peerBrain, CancellationToken ct = default)
    {
        if (services?.GetService(typeof(IFriendPeerDirectory)) is not IFriendPeerDirectory peerDirectory)
        {
            return null;
        }

        try
        {
            using var module = OwnModuleIfForeign();
            var account = await peerDirectory.AccountOf(Id.Brain, peerBrain, ct);
            return account?.Value;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogSwallowed(error, "resolving peer account from friend roster");
            return null;
        }
    }

    private async Task RequireInvitationTo(ConversationInvite invite)
    {
        if (FromOwnRoster()) { return; }
        if (services?.GetService(typeof(IBrainOwners)) is not IBrainOwners owners) { throw FriendRelay.Refused(); }
        var (account, brain) = await FriendRelay.Require(Context.Caller, IConversation.Type, owners, ResolvePeerAccountAsync);
        if (brain == Id.Brain
            || invite.Brain != brain
            || !string.Equals(invite.By.Value, account, StringComparison.Ordinal)
            || Context.Caller?.Via is not { } relay
            || !string.Equals(relay.Key, IConversation.KeyOf(invite.Conversation), StringComparison.Ordinal)
            || !await IsFriendAsync(new AccountId(account)))
        {
            throw FriendRelay.Refused();
        }
    }

    private async Task RequireNoticeOf(NeuronId thread)
    {
        if (Context.Caller is not { } caller) { throw FriendRelay.Refused(); }
        if (caller.Via is not { } relay || relay.Brain == Id.Brain)
        {
            if (caller.Brain == Id.Brain) { return; }
            if (caller.Inside && caller.Via is { } ownThread
                && string.Equals(ownThread.Type, IConversation.Type, StringComparison.Ordinal)
                && ownThread == thread)
            {
                return;
            }
            throw FriendRelay.Refused();
        }

        if (!caller.Inside
            || !string.Equals(relay.Type, IConversation.Type, StringComparison.Ordinal)
            || relay != thread)
        {
            throw FriendRelay.Refused();
        }

        var host = services?.GetService(typeof(IBrainOwners)) is IBrainOwners owners
            ? await owners.OwnerAccount(relay.Brain)
            : null;
        if (string.IsNullOrEmpty(host))
        {
            host = await ResolvePeerAccountAsync(relay.Brain);
        }
        if (string.IsNullOrEmpty(host) || !await IsFriendAsync(new AccountId(host)))
        {
            throw FriendRelay.Refused();
        }
    }
}
