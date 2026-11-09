using DigitalBrain.Brains;
using DigitalBrain.Log;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Flutter.Card;
using IntoChat.OS.Modules.Flutter.Person;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Flutter.Space;

internal sealed class CircleMembers(ICapabilityBroker broker, IServiceProvider plain)
{
    internal sealed record Friend(string Account, string Brain, string[] Scopes);

    public async Task<Friend[]> FriendsOf(string brain)
    {
        Dictionary<string, (string Account, List<string> Scopes)> friends = new(StringComparer.Ordinal);

        // 1. Direct Friends stored in the personal brain's ProfileNeuron (IProfile).
        try
        {
            var profile = await SiloNeurons.Get<IProfile>(plain, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "os.profile", "profile")).Read();
            foreach (var direct in profile.Friends ?? [])
            {
                if (string.IsNullOrWhiteSpace(direct.BrainId) || direct.BrainId == brain) { continue; }
                if (!friends.TryGetValue(direct.BrainId, out var entry))
                {
                    entry = (direct.Handle, []);
                    friends.Add(direct.BrainId, entry);
                }
                var scope = string.IsNullOrWhiteSpace(direct.Scope) ? "friends" : direct.Scope;
                if (!entry.Scopes.Contains(scope, StringComparer.Ordinal))
                {
                    entry.Scopes.Add(scope);
                }
            }
        }
        catch (Exception error)
        {
            NullLogger.Instance.LogSwallowed(error, "Host without AppsModule or uninitialized profile falls back to identity directory.");
        }

        // 2. Fallback: shared personal/circle memberships in the identity directory.
        var directory = await broker.Identity();
        var account = await directory.OwnerOfBrain(brain);
        if (account is not null)
        {
            foreach (var entry in (await directory.BrainsOf(account.OwnerPrincipalId)).Where(b => b.Kind == BrainKind.Circle))
            {
                if ((await directory.Membership(entry.Id, account.OwnerPrincipalId))?.AccountId != account.AccountId) { continue; }
                string[] members;
                try { members = await SiloNeurons.Get<ICircleStateReader>(plain, ICircle.Key(entry.Id)).Members(); }
                catch (KeyNotFoundException error) { NullLogger.Instance.LogSwallowed(error, "Circle state not yet initialized."); continue; }
                if (!members.Contains(account.AccountId, StringComparer.Ordinal)) { continue; }
                foreach (var member in members.Where(m => m != account.AccountId))
                {
                    if (await directory.ReadAccount(member) is not { } memberAccount
                        || (await directory.Membership(entry.Id, memberAccount.OwnerPrincipalId))?.AccountId != member) { continue; }
                    foreach (var personal in await PersonalBrains(member))
                    {
                        if (!friends.TryGetValue(personal, out var friend)) { friend = (member, []); friends.Add(personal, friend); }
                        if (!friend.Scopes.Contains(entry.Id, StringComparer.Ordinal)) { friend.Scopes.Add(entry.Id); }
                    }
                }
            }
        }

        return friends.Select(pair => new Friend(pair.Value.Account, pair.Key, pair.Value.Scopes.ToArray())).ToArray();
    }

    public async Task<string[]> PersonalBrains(string accountId)
    {
        var directory = await broker.Identity();
        if (await directory.ReadAccount(accountId) is not { } account) { return []; }
        List<string> result = [];
        foreach (var brain in (await directory.BrainsOf(account.OwnerPrincipalId)).Where(b => b.Kind == BrainKind.Personal))
        {
            var member = await directory.Membership(brain.Id, account.OwnerPrincipalId);
            if (member?.AccountId == accountId && member.Role == MemberRole.Owner) { result.Add(brain.Id); }
        }
        return result.ToArray();
    }

    public async Task<ProfileState?> TryReadProfile(string brain)
    {
        try
        {
            var profile = await SiloNeurons.Get<IProfile>(plain, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "os.profile", "profile")).Read();
            if (!string.IsNullOrWhiteSpace(profile.Handle) || !string.IsNullOrWhiteSpace(profile.DisplayName) || (profile.Posts ?? []).Length > 0)
            {
                return profile;
            }
        }
        catch (Exception error)
        {
            NullLogger.Instance.LogSwallowed(error, "ProfileNeuron not registered or initialized.");
        }
        return null;
    }

    public async Task<PersonState> ReadPerson(string brain)
    {
        var profileKey = brain + "/profile";
        if (await TryReadProfile(brain) is { } profile)
        {
            var displayName = !string.IsNullOrWhiteSpace(profile.DisplayName)
                ? profile.DisplayName
                : !string.IsNullOrWhiteSpace(profile.Handle)
                    ? $"@{profile.Handle}"
                    : "Friend";
            return new PersonState
            {
                Name = profileKey,
                Version = Math.Max(1, (profile.Posts ?? []).Length + (profile.Friends ?? []).Length),
                DisplayName = displayName,
                AvatarUrl = profile.AvatarUrl ?? "",
            };
        }
        return await SiloNeurons.Get<ISharedPersonReader>(plain, profileKey).Read();
    }

    public async Task<CardState> ReadPostCard(string key)
    {
        if (PostBrain(key) is { } brain && await TryReadProfile(brain) is { } profile)
        {
            var postId = key[(brain.Length + 7)..];
            var post = (profile.Posts ?? []).FirstOrDefault(item => string.Equals(item.Id, postId, StringComparison.Ordinal));
            if (post is not null)
            {
                var children = (post.EmbeddedApps ?? [])
                    .Select(child => new UiChildRef("viewport", child.TargetNeuronUri))
                    .ToList();
                return new CardState
                {
                    Name = key,
                    Version = 1,
                    Title = string.IsNullOrWhiteSpace(post.Title) ? post.Content : post.Title,
                    Body = post.Content,
                    Children = children,
                };
            }
        }
        return await SiloNeurons.Get<ISharedCardReader>(plain, key).Read();
    }

    public async Task<string[]> Posts(Friend friend)
    {
        List<string> posts = [];

        // Read published App-Posts directly from the friend's ProfileNeuron (IProfile).
        if (await TryReadProfile(friend.Brain) is { } profile)
        {
            foreach (var post in profile.Posts ?? [])
            {
                var key = $"{friend.Brain}/posts/{post.Id}";
                if (!posts.Contains(key, StringComparer.Ordinal))
                {
                    posts.Add(key);
                }
            }
        }

        return posts.ToArray();
    }

    public async Task<SpaceComposer.SpaceEntry[]> Entries(string viewer, string focus)
    {
        List<SpaceComposer.SpaceEntry> entries = [];
        foreach (var friend in await FriendsOf(viewer))
        {
            var profileKey = friend.Brain + "/profile";
            var profile = await ReadPerson(friend.Brain);
            entries.Add(new(profileKey, string.IsNullOrWhiteSpace(profile.DisplayName) ? "Friend" : profile.DisplayName, "Friend",
                new(0, profile.Version, new(new Sphere("person", []), Side: new("collection", friend.Brain + "/posts"), Main: new("person", profileKey)), null, []), []));
            if (focus != profileKey && PostBrain(focus) != friend.Brain) { continue; }
            foreach (var key in await Posts(friend))
            {
                var card = await ReadPostCard(key);
                entries.Add(new(key, card.Title, "Post", new(0, card.Version, new(new Sphere("post", []), Main: new("card", key)), null, []), []));
            }
        }
        return entries.ToArray();
    }

    public static string? PostBrain(string key)
    {
        var marker = key.IndexOf("/posts/", StringComparison.Ordinal);
        if (marker <= 0 || marker + 7 == key.Length || !key[(marker + 7)..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) { return null; }
        return key[..marker];
    }
}
