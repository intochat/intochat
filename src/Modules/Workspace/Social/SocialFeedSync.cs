using DigitalBrain;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Social;

/// <summary>One post merged into a brain's social feed along with its owning brain identity.</summary>
internal sealed record SocialFeedEntry(string OwnerBrain, SocialPost Post, bool IsOwn);

/// <summary>Result of a parallel cross-brain social feed cursor sync.</summary>
internal sealed record SocialFeedSyncResult(
    SocialFeedEntry[] Posts,
    IReadOnlyDictionary<string, long> NextCursors,
    string[] FailedBrains);

/// <summary>
/// Parallelizes cross-brain social feed cursor sync using <see cref="Task.WhenAll"/>
/// with per-friend error isolation and strict <see cref="CancellationToken"/> propagation.
/// </summary>
internal static class SocialFeedSync
{
    public static async Task<SocialFeedSyncResult> SyncAsync(
        BrainId ownBrain,
        IPosts? ownPosts,
        IReadOnlyList<Friend> roster,
        Func<BrainId, IPosts> resolveFriendPosts,
        IReadOnlyDictionary<string, long>? cursors = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(resolveFriendPosts);
        ct.ThrowIfCancellationRequested();

        var tasks = new List<Task<BrainFeedSlice>>();
        var ownBrainValue = ownBrain.Value;

        if (ownPosts is not null)
        {
            var ownCursor = cursors is not null && cursors.TryGetValue(ownBrainValue, out var c) ? c : 0L;
            tasks.Add(SyncOneBrainAsync(ownBrainValue, ownPosts, ownCursor, isOwn: true, ct));
        }

        var seenBrains = new HashSet<string>(StringComparer.Ordinal) { ownBrainValue };
        foreach (var friend in roster)
        {
            var friendBrainValue = friend.Brain.Value;
            if (string.IsNullOrWhiteSpace(friendBrainValue) || !seenBrains.Add(friendBrainValue))
            {
                continue;
            }

            var friendCursor = cursors is not null && cursors.TryGetValue(friendBrainValue, out var c) ? c : 0L;
            var friendPosts = resolveFriendPosts(friend.Brain);
            tasks.Add(SyncOneBrainAsync(friendBrainValue, friendPosts, friendCursor, isOwn: false, ct));
        }

        var slices = await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var merged = new List<SocialFeedEntry>();
        var nextCursors = new Dictionary<string, long>(StringComparer.Ordinal);
        var failedBrains = new List<string>();

        foreach (var slice in slices)
        {
            nextCursors[slice.BrainId] = slice.NextCursor;
            if (slice.Failed)
            {
                failedBrains.Add(slice.BrainId);
                continue;
            }

            merged.AddRange(slice.Entries);
        }

        var ordered = merged
            .OrderByDescending(static e => e.Post.CreatedAt)
            .ThenByDescending(static e => e.Post.Sequence)
            .ThenBy(static e => e.OwnerBrain, StringComparer.Ordinal)
            .ToArray();

        return new SocialFeedSyncResult(ordered, nextCursors, [.. failedBrains]);
    }

    private static async Task<BrainFeedSlice> SyncOneBrainAsync(
        string brainId,
        IPosts posts,
        long cursor,
        bool isOwn,
        CancellationToken ct)
    {
        try
        {
            var page = await posts.Since(cursor, ct).ConfigureAwait(false);
            var entries = page.Posts
                .Select(p => new SocialFeedEntry(brainId, p, isOwn))
                .ToArray();
            return new BrainFeedSlice(brainId, entries, Math.Max(cursor, page.NextCursor), Failed: false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For(typeof(SocialFeedSync)).LogDebug(error, "Social feed sync failed for brain {BrainId}.", brainId);
            return new BrainFeedSlice(brainId, [], cursor, Failed: true);
        }
    }

    private sealed record BrainFeedSlice(string BrainId, SocialFeedEntry[] Entries, long NextCursor, bool Failed);
}
