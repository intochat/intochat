using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Apps.Ino;

public sealed partial class InoApp
{
    private ThreadShelf? ThreadShelfSnapshot { get; set; }
    private ThreadPage? ArchivedSessionsPage { get; set; }

    internal ThreadShelf? CurrentShelf => ThreadShelfSnapshot;
    internal ThreadPage? CurrentArchivedSessions => ArchivedSessionsPage;

    private async Task ActivateArcSidebarAsync()
    {
        await TryOptionalAsync("thread-directory-follow", async () =>
        {
            await Follow<IThreadDirectory, ThreadDirectoryChanged>(IThreadDirectory.DefaultKey);
            await Follow<IThreadDirectory, ThreadRevisionChanged>(IThreadDirectory.DefaultKey);
        });
        await RefreshShelfAsync(commit: false);
    }

    private async Task RefreshShelfAsync(bool commit = true)
    {
        var shelf = await ReadOptionalAsync<ThreadShelf?>("thread-shelf", null, async () => await SessionList.Shelf());
        var changed = false;
        if (shelf is not null)
        {
            changed = ShelfChanged(ThreadShelfSnapshot, shelf);
            ThreadShelfSnapshot = shelf;
            SessionsPage = new ThreadPage(shelf.Entries, shelf.Entries.Length);
        }

        if (State.HistoryOpen)
        {
            ArchivedSessionsPage = await ReadOptionalAsync<ThreadPage?>("thread-history", null, async () => await SessionList.List(archived: true, skip: 0, take: 50));
            changed = true;
        }

        if (commit && shelf is not null && changed)
        {
            await Commit(State);
        }
    }

    private static bool ShelfChanged(ThreadShelf? previous, ThreadShelf next)
        => previous is null
            || previous.TotalUnread != next.TotalUnread
            || !previous.Folders.SequenceEqual(next.Folders, StringComparer.Ordinal)
            || !EntriesVisiblyEqual(previous.Favourites, next.Favourites)
            || !EntriesVisiblyEqual(previous.Pinned, next.Pinned)
            || !EntriesVisiblyEqual(previous.Entries, next.Entries);

    private static bool EntriesVisiblyEqual(ThreadEntry[] left, ThreadEntry[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                || !string.Equals(a.Title, b.Title, StringComparison.Ordinal)
                || !string.Equals(a.Folder, b.Folder, StringComparison.Ordinal)
                || a.IsFavourite != b.IsFavourite
                || a.IsPinned != b.IsPinned
                || a.Archived != b.Archived
                || a.UnreadCount != b.UnreadCount
                || a.Kind != b.Kind
                || a.TrustPosture != b.TrustPosture
                || a.Thread != b.Thread)
            {
                return false;
            }
        }

        return true;
    }

    private Task MarkCurrentThreadReadAsync(long revision)
    {
        if (revision <= 0 || string.IsNullOrWhiteSpace(State.ConversationId))
        {
            return Task.CompletedTask;
        }

        return TryOptionalAsync("thread-mark-read", () => SessionList.MarkRead(State.ConversationId, revision));
    }

    [On]
    public Task OnThreadDirectoryChanged(ThreadDirectoryChanged? changed)
        => changed is not null ? RefreshShelfAsync(commit: true) : Task.CompletedTask;

    [On]
    public Task OnThreadRevisionChanged(ThreadRevisionChanged? changed)
        => changed is not null ? RefreshShelfAsync(commit: true) : Task.CompletedTask;

    [On]
    public Task ToggleSidebar(InoToggleSidebar request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Commit(State with { SidebarCollapsed = !State.SidebarCollapsed, NewMenuOpen = false });
    }

    [On]
    public Task ToggleNewMenu(InoToggleNewMenu request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Commit(State with { NewMenuOpen = !State.NewMenuOpen });
    }

    [On]
    public async Task ToggleHistory(InoToggleHistory request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextOpen = !State.HistoryOpen;
        if (nextOpen)
        {
            ArchivedSessionsPage = await ReadOptionalAsync<ThreadPage?>("thread-history", new ThreadPage([], 0), async () => await SessionList.List(archived: true, skip: 0, take: 50));
        }
        await Commit(State with { HistoryOpen = nextOpen });
    }

    [On]
    public async Task ToggleFavourite(InoToggleFavourite request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetId = string.IsNullOrWhiteSpace(request.ConversationId) ? State.ConversationId : request.ConversationId.Trim();
        await TryOptionalAsync("thread-favourite", () => SessionList.SetFavourite(targetId, request.Favourite));
        await RefreshShelfAsync(commit: true);
    }

    [On]
    public async Task TogglePin(InoTogglePin request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetId = string.IsNullOrWhiteSpace(request.ConversationId) ? State.ConversationId : request.ConversationId.Trim();
        await TryOptionalAsync("thread-pin", () => SessionList.SetPinned(targetId, request.Pinned));
        await RefreshShelfAsync(commit: true);
    }

    [On]
    public async Task SetFolder(InoSetFolder request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetId = string.IsNullOrWhiteSpace(request.ConversationId) ? State.ConversationId : request.ConversationId.Trim();
        await TryOptionalAsync("thread-folder", () => SessionList.SetFolder(targetId, request.Folder));
        await RefreshShelfAsync(commit: true);
    }

    [On]
    public async Task ArchiveThread(InoArchiveThread request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetId = string.IsNullOrWhiteSpace(request.ConversationId) ? State.ConversationId : request.ConversationId.Trim();
        await TryOptionalAsync("thread-archive", () => SessionList.Archive(targetId));
        await RefreshShelfAsync(commit: true);
    }

    [On]
    public async Task RestoreThread(InoRestoreThread request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return;
        }
        await TryOptionalAsync("thread-unarchive", () => SessionList.Unarchive(request.ConversationId.Trim()));
        ArchivedSessionsPage = await ReadOptionalAsync<ThreadPage?>("thread-history", new ThreadPage([], 0), async () => await SessionList.List(archived: true, skip: 0, take: 50));
        await RefreshShelfAsync(commit: true);
    }

    [On]
    public async Task ClearToday(InoClearToday request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shelf = ThreadShelfSnapshot ?? await ReadOptionalAsync<ThreadShelf?>("thread-shelf", null, async () => await SessionList.Shelf());
        if (shelf is not null)
        {
            var today = shelf.Entries
                .Where(static e => e.Kind == ThreadKind.DirectAssistant && !e.IsFavourite && !e.IsPinned && string.IsNullOrWhiteSpace(e.Folder))
                .ToArray();
            foreach (var entry in today)
            {
                await TryOptionalAsync("thread-clear-today", () => SessionList.Archive(entry.Id));
            }
        }
        await RefreshShelfAsync(commit: true);
    }

    [On]
    public async Task CreateRoom(InoCreateRoom request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Shared Room" : request.Title.Trim();
        var kind = string.Equals(request.Kind, "DirectPerson", StringComparison.OrdinalIgnoreCase)
            ? ThreadKind.DirectPerson
            : ThreadKind.SharedRoom;
        var roomId = await ReadOptionalAsync<NeuronId?>("create-shared-room", null, async () => await SessionList.CreateShared(title, kind));
        if (roomId is null)
        {
            await Commit(State with { NewMenuOpen = false });
            return;
        }
        await TryOptionalAsync("configure-shared-room", () => Conversations[roomId.Value.Key].Configure(ThreadTrustPosture.CrossBrainShared, kind, title));
        await RefreshShelfAsync(commit: false);
        var entryId = Threads.EntryIdFor(CurrentBrain, roomId.Value);
        await Commit(State with { NewMenuOpen = false });
        await SwitchConversationAsync(entryId, allowSameThreadRefresh: true);
    }

    [On]
    public Task OpenPinnedApps(InoOpenPinnedApps request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var artifact = State.ActiveArtifact ?? new InoDrawerArtifact(
            Id: $"pinned-apps:{State.ConversationId}",
            Title: string.IsNullOrWhiteSpace(State.Title) ? "Pinned Apps" : $"{State.Title} · Pinned Apps",
            Kind: "pinned-apps",
            Status: "done");
        return Commit(State with
        {
            DrawerOpen = true,
            DrawerTab = InoArcSidebar.DrawerTabPinnedApps,
            ActiveArtifact = artifact,
        });
    }
}
