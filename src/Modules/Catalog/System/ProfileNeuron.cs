using DigitalBrain.Log;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("os.profile")]
public sealed class ProfileNeuron(ILogger<ProfileNeuron>? logger = null) : DigitalBrain.Neurons.Neuron<ProfileState>, IProfile
{
    private ILogger Log => (ILogger?)logger ?? OsConsoleLog.For<ProfileNeuron>();
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

    public async Task<ProfileState> ConfirmFriend(string account, string friendBrainId, string handle, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendBrainId);
        if (Context.Caller?.Via is not { Type: FriendLinksDefaults.TypeName } via || via.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException("A friend is confirmed only by this brain's friend links.");
        }

        var brain = friendBrainId.Trim();
        var normalizedHandle = Short(string.IsNullOrWhiteSpace(handle) ? brain : handle.Trim().TrimStart('@'));
        var name = Short(string.IsNullOrWhiteSpace(displayName) ? normalizedHandle : displayName.Trim());
        var friend = new ProfileFriend(brain, normalizedHandle, name, "friends", Context.Time.GetUtcNow(), account, Confirmed: true);
        // The confirmed row replaces a typed row for the same brain and any earlier row of the same account.
        var others = (State.Friends ?? [])
            .Where(item => !string.Equals(item.Account, account, StringComparison.Ordinal)
                && !string.Equals(item.BrainId, brain, StringComparison.OrdinalIgnoreCase));
        var next = State with { Friends = [.. others.Prepend(friend).Take(200)] };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }

    public async Task<ProfileState> RemoveFriendAccount(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (Context.Caller?.Via is not { Type: FriendLinksDefaults.TypeName } via || via.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException("A friend is removed by account only by this brain's friend links.");
        }

        var remaining = (State.Friends ?? []).Where(item => !string.Equals(item.Account, account, StringComparison.Ordinal)).ToArray();
        if (remaining.Length == (State.Friends ?? []).Length) { return State; }
        var next = State with { Friends = remaining };
        await Save(next, new ProfileChanged(next.Handle, next.Posts.Length));
        return next;
    }

    private static string Short(string value) => value.Length <= 64 ? value : value[..64];

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

    public async Task<ProfileState> PublishPost(string id, string content, string circle = "public")
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
            TemplateSource: "",
            EmbeddedApps: [],
            AuthorBrainId: Id.Brain.Value);
        return await PublishAppPost(item);
    }

    public async Task<ProfileState> PublishAppPost(ProfilePostItem post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentException.ThrowIfNullOrWhiteSpace(post.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(post.Content);

        var templateAppId = string.IsNullOrWhiteSpace(post.TemplateAppId) ? "friends.post-template" : post.TemplateAppId.Trim();
        var unitFiles = post.UnitFiles;
        var templateSource = post.TemplateSource ?? "";

        if ((unitFiles is null || unitFiles.Count == 0) && !string.Equals(templateAppId, "friends.post-template", StringComparison.Ordinal))
        {
            var resolvedFiles = await TryReadStudioFilesAsync(templateAppId);
            if (resolvedFiles.Count > 0)
            {
                unitFiles = resolvedFiles;
                if (string.IsNullOrWhiteSpace(templateSource))
                {
                    templateSource = string.Join("\n\n", resolvedFiles
                        .Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal))
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => kv.Value));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(templateSource))
        {
            templateSource = $"[App(\"post.{post.Id.Trim()}\")] public sealed class PostTemplateApp : App<PostState> {{ }}";
        }

        var normalized = post with
        {
            Id = post.Id.Trim(),
            Content = post.Content.Trim(),
            Circle = string.IsNullOrWhiteSpace(post.Circle) ? "public" : post.Circle.Trim(),
            Title = string.IsNullOrWhiteSpace(post.Title) ? post.Content.Trim() : post.Title.Trim(),
            TemplateAppId = templateAppId,
            TemplateSource = templateSource,
            EmbeddedApps = post.EmbeddedApps ?? [],
            UnitFiles = unitFiles,
            AuthorBrainId = string.IsNullOrWhiteSpace(post.AuthorBrainId) ? Id.Brain.Value : post.AuthorBrainId.Trim(),
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

        var brainId = Id.Brain.Value ?? string.Empty;
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
                    new("canvas", "markdraw", "Markdraw Canvas", IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, "markdraw").Key),
                ],
                AuthorBrainId: brainId);

        var filesToDeploy = source.UnitFiles;
        if ((filesToDeploy is null || filesToDeploy.Count == 0) && !string.Equals(source.TemplateAppId, "friends.post-template", StringComparison.Ordinal))
        {
            var studioFiles = await TryReadStudioFilesAsync(source.TemplateAppId);
            if (studioFiles.Count > 0)
            {
                filesToDeploy = studioFiles;
            }
        }

        if (filesToDeploy is { Count: > 0 } && filesToDeploy.Keys.Any(k => k.EndsWith(".cs", StringComparison.Ordinal)))
        {
            if (AppPackageExtensions.TryFromFiles(newPostId.Trim(), "1.0.0", filesToDeploy) is { } pkg)
            {
                try
                {
                    var remixed = pkg with { Key = newPostId.Trim() };
                    await Context.Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Host.Primitives.AppGrainType.Name, $"apps/{newPostId.Trim()}")).Import(remixed.ToAppBundle());
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Log.LogSwallowed(error, "IApp unavailable when importing forked AppPackage.");
                }
            }

            try
            {
                var studio = Context.Get<ISynapseStudio>(ISynapseStudio.Key(brainId));
                await studio.DeployFiles(source.TemplateAppId, filesToDeploy, new Dictionary<string, string>(StringComparer.Ordinal));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "SynapseStudio unavailable during profile post fork.");
            }
        }

        var forked = source with
        {
            Id = newPostId.Trim(),
            Title = string.IsNullOrWhiteSpace(remixTitle) ? $"Remix · {source.Title}" : remixTitle.Trim(),
            Content = string.IsNullOrWhiteSpace(remixTitle) ? $"{source.Content} (Forked App Template)" : $"{remixTitle.Trim()} — {source.Content}",
            ForkedFrom = $"@{State.Handle}/{source.Id}",
            UnitFiles = filesToDeploy ?? source.UnitFiles,
            AuthorBrainId = brainId,
            CreatedAt = Context.Time.GetUtcNow(),
        };
        return await PublishAppPost(forked);
    }

    private async Task<Dictionary<string, string>> TryReadStudioFilesAsync(string alias)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(alias)) { return files; }
        try
        {
            var brainId = Id.Brain.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(brainId)) { return files; }
            var studio = Context.Get<ISynapseStudio>(ISynapseStudio.Key(brainId));
            foreach (var (path, text) in await studio.SourceFiles(alias)) { files[path] = text; }
            if (files.Count > 0)
            {
                foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "Failed to read unit source files from SynapseStudio for profile post.");
        }

        if (files.Count == 0 && !string.IsNullOrWhiteSpace(Id.Brain.Value))
        {
            try
            {
                var exported = await Context.Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Host.Primitives.AppGrainType.Name, $"apps/{alias}")).Export();
                if (exported?.Files is { Count: > 0 })
                {
                    foreach (var (path, text) in exported.Files)
                    {
                        files[path] = text;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "Failed to read exported AppBundle files for profile post.");
            }
        }

        return files;
    }
}
