using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

internal sealed record PostsState(SocialPost[] Posts, long NextSequence)
{
    public PostsState() : this([], 0) { }
}

// Who may see a post: the one rule, asked for a person of another brain who is the author's friend at that moment (the
// neuron establishes that first). The brain's own side sees every post and is not asked here.
internal static class PostAudience
{
    public static bool Admits(SocialPost post, string friend) => post.Audience switch
    {
        // A post stored before audiences were a rule: the account list frozen when it was published.
        null => (post.AudienceAccounts ?? []).Any(account => string.Equals(account, friend, StringComparison.Ordinal)),
        // Public means the same as Friends until a brain can be reached by people who are not friends.
        Audience.Public or Audience.Friends => true,
        // The list is frozen; being on it does not replace being a friend.
        Audience.Only only => (only.Accounts ?? []).Any(account => string.Equals(account.Value, friend, StringComparison.Ordinal)),
        _ => false,
    };
}

// A person's posts, in their own brain. The kernel admits a person of another brain who holds an offer on this neuron;
// the neuron then shows each post only to a person its audience admits who is the author's friend at that moment.
[Neuron(PostsNeuron.PostsType)]
internal sealed class PostsNeuron(IServiceProvider? services = null) : Neuron<PostsState>, IPosts
{
    public const string PostsType = "social.posts";

    // Whoever reads or writes, taken from the caller the kernel stamped and from nothing a caller passes.
    private readonly record struct Reader(string Account, bool OwnSide);

    // Only the roster's own fact is heard: a signal of the same name from another neuron never arrives here.
    private Task HearTheRoster() => Context.Wires.Ensure<FriendAdded>(FriendsDefaults.Id(Id.Brain.Value));

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken).ConfigureAwait(true);
        if (State.Posts.Length == 0) { return; }
        // As this brain's own module: the call that wakes the neuron may come from a person of another brain. A roster
        // that cannot be heard now is heard from the next publish on; nothing is shown to anyone because of it.
        try
        {
            using var own = AsPostsModule();
            await HearTheRoster().ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<PostsNeuron>().LogSwallowed(error, "the post store could not subscribe to its roster on activation");
        }
    }

    // The roster added a friend: the kernel lets them in when a post is theirs to see. Which posts, the audience rule
    // decides on every read.
    [On]
    public async Task On(FriendAdded added)
    {
        ArgumentNullException.ThrowIfNull(added);
        var account = added.Account.Value;
        if (string.IsNullOrEmpty(account) || !State.Posts.Any(post => PostAudience.Admits(post, account))) { return; }
        using var own = AsPostsModule();
        await OfferTo(account, CancellationToken.None).ConfigureAwait(true);
    }

    public async Task<string> Publish(PostDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Text);
        var author = RequireOwnSide().Account;

        var listed = (draft.AudienceAccounts ?? [])
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Select(static a => a.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var audience = Frozen(draft.Audience);

        var sharedApp = SanitizeSharedApp(draft.SharedApp);
        var sequence = State.NextSequence + 1;
        var postId = string.IsNullOrWhiteSpace(draft.Id) ? Guid.NewGuid().ToString("N") : draft.Id.Trim();
        // Publishing under an id the store holds replaces that post: it keeps its reactions, comments and date, and takes
        // a new sequence, so a reader with a cursor gets the new version.
        var replaced = State.Posts.FirstOrDefault(existing => string.Equals(existing.Id, postId, StringComparison.Ordinal));
        var post = new SocialPost(
            postId,
            author,
            draft.Text.Trim(),
            listed,
            replaced?.Reactions ?? [],
            replaced?.Comments ?? [],
            sequence,
            draft.CreatedAt ?? replaced?.CreatedAt ?? Context.Time.GetUtcNow(),
            sharedApp,
            string.IsNullOrWhiteSpace(draft.Title) ? null : draft.Title.Trim(),
            audience,
            draft.EmbeddedApps is { Length: > 0 } embedded ? [.. embedded] : null,
            string.IsNullOrWhiteSpace(draft.ForkedFrom) ? null : draft.ForkedFrom.Trim(),
            string.IsNullOrWhiteSpace(draft.TemplateSource) ? null : draft.TemplateSource);

        await Store(new PostsState([.. State.Posts.Where(existing => !ReferenceEquals(existing, replaced)), post], sequence)).ConfigureAwait(true);
        await HearTheRoster().ConfigureAwait(true);

        // An offer opens the kernel's gate for a person the post may be shown to: the accounts the draft names and, for a
        // post meant for friends, every friend the roster lists now. A friend added later is offered when the roster
        // says so.
        IEnumerable<string> admitted = audience switch
        {
            Audience.Only only => listed.Concat(only.Accounts.Select(static a => a.Value)),
            Audience.Public or Audience.Friends => listed.Concat((await FriendsNow().ConfigureAwait(true)).Select(static friend => friend.Account.Value)),
            _ => listed,
        };
        foreach (var account in admitted.Distinct(StringComparer.Ordinal).ToArray())
        {
            if (!string.Equals(account, author, StringComparison.Ordinal)) { await OfferTo(account, ct).ConfigureAwait(true); }
        }

        return postId;
    }

    public async Task<PostPage> Since(long cursor = 0, CancellationToken ct = default)
    {
        var reader = await WhoAsks().ConfigureAwait(true);
        var visible = State.Posts
            .Where(p => p.Sequence > cursor && Sees(reader, p))
            .OrderBy(static p => p.Sequence)
            .ToArray();

        var nextCursor = visible.Length > 0 ? visible[^1].Sequence : cursor;
        return new PostPage(visible, nextCursor);
    }

    public async Task React(string postId, string reaction, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);

        var reader = await WhoAsks().ConfigureAwait(true);
        var index = FindVisiblePost(reader, postId);
        var post = State.Posts[index];

        var kind = reaction.Trim();
        var updatedReactions = post.Reactions
            .Where(r => !(string.Equals(r.Account, reader.Account, StringComparison.Ordinal)
                && string.Equals(r.Kind, kind, StringComparison.Ordinal)))
            .Append(new PostReaction(reader.Account, kind, Context.Time.GetUtcNow()))
            .ToArray();

        var updatedPosts = State.Posts.ToArray();
        updatedPosts[index] = post with { Reactions = updatedReactions };
        await Store(State with { Posts = updatedPosts }).ConfigureAwait(true);
    }

    public async Task<string> Comment(string postId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var reader = await WhoAsks().ConfigureAwait(true);
        var index = FindVisiblePost(reader, postId);
        var post = State.Posts[index];

        var commentId = Guid.NewGuid().ToString("N");
        var comment = new PostComment(commentId, reader.Account, text.Trim(), Context.Time.GetUtcNow());

        var updatedPosts = State.Posts.ToArray();
        updatedPosts[index] = post with { Comments = [.. post.Comments, comment] };
        await Store(State with { Posts = updatedPosts }).ConfigureAwait(true);
        return commentId;
    }

    // The audience rule is applied here and nowhere else: Since, React and Comment all pass through WhoAsks and Sees.
    private static bool Sees(Reader reader, SocialPost post) => reader.OwnSide || PostAudience.Admits(post, reader.Account);

    // The brain's own side (its owner, or an app, an agent or a module acting inside the brain), or a person of another
    // brain whom this brain's roster lists as a friend now. An offer is not friendship and can outlive it. A call that
    // carries no caller is refused.
    private async Task<Reader> WhoAsks()
    {
        var caller = RequireCaller();
        if (IsSameBrainCaller()) { return new(caller.For?.Account ?? Id.Brain.Value, OwnSide: true); }

        var account = caller.For?.Account;
        if (string.IsNullOrEmpty(account) || !await RoomFriends.IsFriend(services, Id.Brain, new AccountId(account)).ConfigureAwait(true))
        {
            throw Refused();
        }
        return new(account, OwnSide: false);
    }

    private Reader RequireOwnSide()
    {
        var caller = RequireSameBrain(() => new UnauthorizedAccessException($"Only brain '{Id.Brain.Value}' may publish posts."));
        return new(caller.For?.Account ?? Id.Brain.Value, OwnSide: true);
    }

    private UnauthorizedAccessException Refused()
        => new($"offer.none: the posts of '{Id.Brain.Value}' are for its owner and the owner's friends.");

    // A post the reader may not see is answered like a post that does not exist.
    private int FindVisiblePost(Reader reader, string postId)
    {
        for (var i = 0; i < State.Posts.Length; i++)
        {
            if (string.Equals(State.Posts[i].Id, postId, StringComparison.Ordinal) && Sees(reader, State.Posts[i]))
            {
                return i;
            }
        }
        throw new KeyNotFoundException($"Post '{postId}' was not found.");
    }

    // The offers registry refuses a person the roster does not list. The post stands; they are offered when they are a
    // friend, and the audience rule refuses them until then.
    private async Task OfferTo(string account, CancellationToken ct)
    {
        try { await Context.Offers.Offer(Id, account, Role.Editor, ct).ConfigureAwait(true); }
        catch (UnauthorizedAccessException refused)
        {
            OsConsoleLog.For<PostsNeuron>().LogSwallowed(refused, "the offers registry refused an offer on the post store");
        }
    }

    // The friends this brain's roster lists, asked as this brain's own module.
    private async Task<Friend[]> FriendsNow()
    {
        if (services?.GetService(typeof(INeuronProxies)) is not INeuronProxies proxies) { return []; }
        using var own = AsPostsModule();
        return await proxies.Get<IFriends>(FriendsDefaults.Id(Id.Brain.Value)).List().ConfigureAwait(true);
    }

    private IDisposable? AsPostsModule()
    {
        var edge = services?.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        return edge?.Enter(edge.Module(PostsType, Id.Brain));
    }

    private static Audience? Frozen(Audience? audience) => audience is Audience.Only only
        ? new Audience.Only([.. (only.Accounts ?? []).Where(static a => !string.IsNullOrWhiteSpace(a.Value)).Select(static a => new AccountId(a.Value.Trim())).Distinct()])
        : audience;

    private static AppDefinitionShare? SanitizeSharedApp(AppDefinitionShare? share)
    {
        if (share is null)
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(share.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(share.Title);
        var files = (share.Files ?? [])
            .Where(static f => f is not null && !string.IsNullOrWhiteSpace(f.Path))
            .Select(static f => new SharedSourceFile(f.Path.Trim(), f.Content ?? string.Empty))
            .ToArray();
        return new AppDefinitionShare(share.AppId.Trim(), share.Title.Trim(), files);
    }
}
