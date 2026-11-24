using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

// The posts a profile kept (ProfileState.Posts) and the post store's record of the same thing. The profile's list is
// copied into the post store once (PostsNeuron) and is not written again; the profile's post methods hand their post to
// the post store. A profile post was never visible outside its brain, so whatever comes from the profile is stored for
// its owner only.
internal static class ProfilePosts
{
    public const string PlainTemplate = "friends.post-template";

    public static Audience OwnerOnly() => new Audience.Only([]);

    // The profile posts the store does not hold yet, as the store keeps them: same id, same date, the next sequence
    // numbers in order of date (oldest first; the profile's array is newest first, so of two posts with one date the later
    // entry is the older). A post without an id is left behind. Of two profile posts with one id the first (newest) is
    // taken. An id the store already holds is the store's: that profile post is not copied.
    public static SocialPost[] NotYetHeld(ProfilePostItem[]? profile, SocialPost[] held, string author, long lastSequence)
    {
        var taken = new HashSet<string>(held.Select(static post => post.Id), StringComparer.Ordinal);
        var fresh = new List<(ProfilePostItem Post, int Index)>();
        var all = profile ?? [];
        for (var index = 0; index < all.Length; index++)
        {
            if (all[index] is { } post && !string.IsNullOrWhiteSpace(post.Id) && taken.Add(post.Id)) { fresh.Add((post, index)); }
        }

        return [.. fresh
            .OrderBy(static entry => entry.Post.CreatedAt)
            .ThenByDescending(static entry => entry.Index)
            .Select((entry, position) => ToStored(entry.Post, author, lastSequence + 1 + position))];
    }

    public static SocialPost ToStored(ProfilePostItem post, string author, long sequence) => new(
        post.Id,
        author,
        post.Content ?? "",
        [],
        [],
        [],
        sequence,
        post.CreatedAt,
        AppOf(post),
        Blank(post.Title),
        OwnerOnly(),
        post.EmbeddedApps is { Length: > 0 } embedded ? [.. embedded] : null,
        Blank(post.ForkedFrom),
        SourceOf(post));

    public static PostDraft ToDraft(ProfilePostItem post) => new(
        post.Content,
        [],
        AppOf(post),
        post.Id,
        Blank(post.Title),
        OwnerOnly(),
        post.EmbeddedApps is { Length: > 0 } embedded ? [.. embedded] : null,
        Blank(post.ForkedFrom),
        post.CreatedAt == default ? null : post.CreatedAt,
        SourceOf(post));

    // A stored post in the profile's shape, for the callers and files that still speak it (the profile's post methods, the
    // portable archive).
    public static ProfilePostItem ToProfile(SocialPost post, string brain)
    {
        Dictionary<string, string>? files = null;
        if (post.SharedApp?.Files is { Length: > 0 } shared)
        {
            files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in shared) { files[file.Path] = file.Content; }
        }

        return new ProfilePostItem(
            post.Id,
            post.Text,
            post.CreatedAt,
            "",
            post.Title ?? post.Text,
            string.IsNullOrWhiteSpace(post.SharedApp?.AppId) ? PlainTemplate : post.SharedApp.AppId,
            post.TemplateSource ?? JoinedSource(files),
            post.EmbeddedApps ?? [],
            post.ForkedFrom,
            files,
            brain)
        {
            Audience = post.Audience ?? new Audience.Only([.. (post.AudienceAccounts ?? []).Select(static account => new AccountId(account))]),
        };
    }

    public static string JoinedSource(IReadOnlyDictionary<string, string>? files) => files is null
        ? ""
        : string.Join("\n\n", files
            .Where(static file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .OrderBy(static file => file.Key, StringComparer.Ordinal)
            .Select(static file => file.Value));

    private static AppDefinitionShare? AppOf(ProfilePostItem post) => string.IsNullOrWhiteSpace(post.TemplateAppId)
        ? null
        : new AppDefinitionShare(
            post.TemplateAppId.Trim(),
            Blank(post.Title) ?? post.Id,
            [.. (post.UnitFiles ?? new Dictionary<string, string>())
                .Where(static file => !string.IsNullOrWhiteSpace(file.Key))
                .OrderBy(static file => file.Key, StringComparer.Ordinal)
                .Select(static file => new SharedSourceFile(file.Key, file.Value ?? ""))]);

    // The profile kept the source text beside the files it was joined from. It is kept only where no file gives it.
    private static string? SourceOf(ProfilePostItem post)
        => string.IsNullOrWhiteSpace(post.TemplateSource) || post.UnitFiles?.Keys.Any(static path => path.EndsWith(".cs", StringComparison.Ordinal)) == true
            ? null
            : post.TemplateSource;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
