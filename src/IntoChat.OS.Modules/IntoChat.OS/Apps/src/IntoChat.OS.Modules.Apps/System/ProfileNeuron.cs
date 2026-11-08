namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("os.profile")]
public sealed class ProfileNeuron : DigitalBrain.Neuron<ProfileState>, IProfile
{
    public Task<ProfileState> Read() => Task.FromResult(State);

    public Task<ProfileState> SetBio(string handle, string bio, string[] pinnedApps)
        => UpdateProfile(handle, State.DisplayName, bio, pinnedApps, State.AvatarUrl);

    public async Task<ProfileState> UpdateProfile(string handle, string displayName, string bio, string[] pinnedApps, string avatarUrl = "")
    {
        ArgumentNullException.ThrowIfNull(pinnedApps);
        var normalizedHandle = string.IsNullOrWhiteSpace(handle) ? State.Handle : handle.Trim().TrimStart('@');
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? (string.IsNullOrWhiteSpace(State.DisplayName) ? normalizedHandle : State.DisplayName)
            : displayName.Trim();
        var next = State with
        {
            Handle = normalizedHandle,
            DisplayName = normalizedDisplayName,
            Bio = bio ?? "",
            AvatarUrl = avatarUrl ?? "",
            PinnedApps = [.. pinnedApps],
            Friends = State.Friends ?? [],
            SharedApps = State.SharedApps ?? [],
        };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }

    public async Task<ProfileState> AddFriend(string friendBrainId, string handle, string displayName, string scope = "friends")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendBrainId);
        var normalizedBrain = friendBrainId.Trim();
        var normalizedHandle = string.IsNullOrWhiteSpace(handle) ? normalizedBrain : handle.Trim().TrimStart('@');
        var normalizedName = string.IsNullOrWhiteSpace(displayName) ? normalizedHandle : displayName.Trim();
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "friends" : scope.Trim().ToLowerInvariant();
        var friend = new ProfileFriend(normalizedBrain, normalizedHandle, normalizedName, normalizedScope, Context.Time.GetUtcNow());
        var existing = (State.Friends ?? [])
            .Where(item => !string.Equals(item.BrainId, normalizedBrain, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Handle, normalizedHandle, StringComparison.OrdinalIgnoreCase))
            .Prepend(friend)
            .Take(200)
            .ToArray();
        var next = State with { Friends = existing };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }

    public async Task<ProfileState> RemoveFriend(string friendBrainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendBrainId);
        var target = friendBrainId.Trim().TrimStart('@');
        var remaining = (State.Friends ?? [])
            .Where(item => !string.Equals(item.BrainId, target, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Handle, target, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var next = State with { Friends = remaining };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }

    public Task<ProfileState> PublishPost(string id, string content, string circle = "public")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var scope = string.IsNullOrWhiteSpace(circle) ? "public" : circle.Trim();
        var item = new ProfilePostItem(
            id.Trim(),
            content.Trim(),
            Context.Time.GetUtcNow(),
            scope,
            Title: content.Trim(),
            TemplateAppId: "friends.post-template",
            TemplateSource: $"[App(\"post.{id.Trim()}\")] public sealed class PostTemplateApp : App<PostState> {{ }}",
            EmbeddedApps: []);
        return PublishAppPost(item);
    }

    public async Task<ProfileState> PublishAppPost(ProfilePostItem post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentException.ThrowIfNullOrWhiteSpace(post.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(post.Content);

        var normalized = post with
        {
            Id = post.Id.Trim(),
            Content = post.Content.Trim(),
            Circle = string.IsNullOrWhiteSpace(post.Circle) ? "public" : post.Circle.Trim(),
            Title = string.IsNullOrWhiteSpace(post.Title) ? post.Content.Trim() : post.Title.Trim(),
            TemplateAppId = string.IsNullOrWhiteSpace(post.TemplateAppId) ? "friends.post-template" : post.TemplateAppId.Trim(),
            TemplateSource = post.TemplateSource ?? "",
            EmbeddedApps = post.EmbeddedApps ?? [],
            CreatedAt = post.CreatedAt == default ? Context.Time.GetUtcNow() : post.CreatedAt,
        };

        var posts = State.Posts
            .Where(existing => !string.Equals(existing.Id, normalized.Id, StringComparison.Ordinal))
            .Prepend(normalized)
            .Take(200)
            .ToArray();
        var next = State with { Posts = posts };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }

    public async Task<ProfileState> ForkPost(string postId, string newPostId, string? remixTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPostId);

        var source = State.Posts.FirstOrDefault(existing => string.Equals(existing.Id, postId.Trim(), StringComparison.Ordinal))
            ?? new ProfilePostItem(
                postId.Trim(),
                $"Remixed app template from {postId.Trim()}",
                Context.Time.GetUtcNow(),
                "friends",
                Title: remixTitle ?? $"Remix · {postId.Trim()}",
                TemplateAppId: "friends.post-template",
                TemplateSource: $"[App(\"post.{newPostId.Trim()}\")] public sealed class PostTemplateApp : App<PostState> {{ }}",
                EmbeddedApps:
                [
                    new("canvas", "markdraw", "Markdraw Canvas", "neuron://local/apps/markdraw"),
                ]);

        var forked = source with
        {
            Id = newPostId.Trim(),
            Title = string.IsNullOrWhiteSpace(remixTitle) ? $"Remix · {source.Title}" : remixTitle.Trim(),
            Content = string.IsNullOrWhiteSpace(remixTitle) ? $"{source.Content} (Forked App Template)" : $"{remixTitle.Trim()} — {source.Content}",
            ForkedFrom = $"@{State.Handle}/{source.Id}",
            CreatedAt = Context.Time.GetUtcNow(),
        };
        return await PublishAppPost(forked);
    }
}
