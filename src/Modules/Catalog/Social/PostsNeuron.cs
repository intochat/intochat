using DigitalBrain.Components;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record PostsState(SocialPost[] Posts, long NextSequence)
{
    public PostsState() : this([], 0) { }
}

[Neuron("social.posts")]
internal sealed class PostsNeuron : Neuron<PostsState>, IPosts
{
    public async Task<string> Publish(PostDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Text);
        RequireOwner();

        var author = ResolveCallerAccount();
        var audience = (draft.AudienceAccounts ?? [])
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Select(static a => a.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var sequence = State.NextSequence + 1;
        var postId = Guid.NewGuid().ToString("N");
        var post = new SocialPost(
            postId,
            author,
            draft.Text.Trim(),
            audience,
            [],
            [],
            sequence,
            Context.Time.GetUtcNow());

        await Store(new PostsState([.. State.Posts, post], sequence)).ConfigureAwait(true);

        foreach (var account in audience)
        {
            if (string.Equals(account, author, StringComparison.Ordinal))
            {
                continue;
            }
            await Context.Offers.Offer(Id, account, Role.Editor, ct).ConfigureAwait(true);
        }

        return postId;
    }

    public Task<PostPage> Since(long cursor = 0, CancellationToken ct = default)
    {
        var isOwner = IsOwner();
        var callerAccount = ResolveCallerAccount();

        var visible = State.Posts
            .Where(p => p.Sequence > cursor && (isOwner || IsInAudience(p, callerAccount)))
            .OrderBy(static p => p.Sequence)
            .ToArray();

        var nextCursor = visible.Length > 0 ? visible[^1].Sequence : cursor;
        return Task.FromResult(new PostPage(visible, nextCursor));
    }

    public async Task React(string postId, string reaction, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);

        var index = FindPostIndex(postId);
        var post = State.Posts[index];
        var callerAccount = RequirePostAudience(post);

        var kind = reaction.Trim();
        var updatedReactions = post.Reactions
            .Where(r => !(string.Equals(r.Account, callerAccount, StringComparison.Ordinal)
                && string.Equals(r.Kind, kind, StringComparison.Ordinal)))
            .Append(new PostReaction(callerAccount, kind, Context.Time.GetUtcNow()))
            .ToArray();

        var updatedPosts = State.Posts.ToArray();
        updatedPosts[index] = post with { Reactions = updatedReactions };
        await Store(State with { Posts = updatedPosts }).ConfigureAwait(true);
    }

    public async Task<string> Comment(string postId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var index = FindPostIndex(postId);
        var post = State.Posts[index];
        var callerAccount = RequirePostAudience(post);

        var commentId = Guid.NewGuid().ToString("N");
        var comment = new PostComment(commentId, callerAccount, text.Trim(), Context.Time.GetUtcNow());

        var updatedPosts = State.Posts.ToArray();
        updatedPosts[index] = post with { Comments = [.. post.Comments, comment] };
        await Store(State with { Posts = updatedPosts }).ConfigureAwait(true);
        return commentId;
    }

    private bool IsOwner() => Context.Caller is null || Context.Caller.Brain == Id.Brain;

    private void RequireOwner()
    {
        if (!IsOwner())
        {
            throw new UnauthorizedAccessException($"Only brain '{Id.Brain.Value}' may publish posts.");
        }
    }

    private string ResolveCallerAccount()
        => Context.Caller?.For?.Account ?? Id.Brain.Value;

    private static bool IsInAudience(SocialPost post, string account)
        => post.AudienceAccounts.Any(a => string.Equals(a, account, StringComparison.Ordinal));

    private string RequirePostAudience(SocialPost post)
    {
        var account = ResolveCallerAccount();
        if (!IsOwner() && !IsInAudience(post, account))
        {
            throw new UnauthorizedAccessException($"offer.none: '{account}' is not in the audience of post '{post.Id}'.");
        }
        return account;
    }

    private int FindPostIndex(string postId)
    {
        for (var i = 0; i < State.Posts.Length; i++)
        {
            if (string.Equals(State.Posts[i].Id, postId, StringComparison.Ordinal))
            {
                return i;
            }
        }
        throw new KeyNotFoundException($"Post '{postId}' was not found.");
    }
}
