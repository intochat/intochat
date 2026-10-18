using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Person;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Authority.Identity;

namespace DigitalBrain.Flutter.Space;

internal sealed class CircleMembers(IGrainFactory grains, IIdentityClient identity)
{
    internal sealed record Friend(string Account, string Brain, string[] Circles);

    public async Task<Friend[]> FriendsOf(string brain)
    {
        // Creation/default installation can rebuild a space while the brain's Create turn is active.
        // Resolve ownership in the local identity directory; reading IBrain here would wait on that same turn.
        var account = await OwnerAccount(brain);
        if (account is null) { return []; }
        Dictionary<string, (string Account, List<string> Circles)> friends = new(StringComparer.Ordinal);
        foreach (var entry in (await identity.BrainsOf(account.OwnerPrincipalId)).Where(b => b.Kind == BrainKind.Circle))
        {
            if ((await identity.Membership(entry.Id, account.OwnerPrincipalId))?.AccountId != account.AccountId) { continue; }
            string[] members;
            try { members = await grains.GetGrain<ICircleStateReader>(ICircle.Key(entry.Id)).Members(); }
            catch (KeyNotFoundException) { continue; }
            if (!members.Contains(account.AccountId, StringComparer.Ordinal)) { continue; }
            foreach (var member in members.Where(m => m != account.AccountId))
            {
                if (await identity.ReadAccount(member) is not { } memberAccount
                    || (await identity.Membership(entry.Id, memberAccount.OwnerPrincipalId))?.AccountId != member) { continue; }
                foreach (var personal in await PersonalBrains(member))
                {
                    if (!friends.TryGetValue(personal, out var friend)) { friend = (member, []); friends.Add(personal, friend); }
                    friend.Circles.Add(entry.Id);
                }
            }
        }
        return friends.Select(pair => new Friend(pair.Value.Account, pair.Key, pair.Value.Circles.ToArray())).ToArray();
    }

    private Task<Account?> OwnerAccount(string brain) => identity.OwnerOfBrain(brain);

    public async Task<string[]> PersonalBrains(string accountId)
    {
        if (await identity.ReadAccount(accountId) is not { } account) { return []; }
        List<string> result = [];
        foreach (var brain in (await identity.BrainsOf(account.OwnerPrincipalId)).Where(b => b.Kind == BrainKind.Personal))
        {
            var member = await identity.Membership(brain.Id, account.OwnerPrincipalId);
            if (member?.AccountId == accountId && member.Role == MemberRole.Owner) { result.Add(brain.Id); }
        }
        return result.ToArray();
    }

    public async Task<string[]> Posts(Friend friend)
    {
        List<string> posts = [];
        foreach (var circle in friend.Circles)
        {
            foreach (var line in (await grains.GetGrain<ICirclePostReader>(ICircleChat.Key(circle)).PostReferences()).Reverse())
            {
                if (line.Speaker != friend.Account || !line.Text.StartsWith("post ", StringComparison.Ordinal)) { continue; }
                var key = line.Text[5..];
                if (PostBrain(key) != friend.Brain) { continue; }
                if (!posts.Contains(key, StringComparer.Ordinal)) { posts.Add(key); }
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
            var profile = await grains.GetGrain<ISharedPersonReader>(profileKey).Read();
            entries.Add(new(profileKey, string.IsNullOrWhiteSpace(profile.DisplayName) ? "Friend" : profile.DisplayName, "Friend",
                new(0, profile.Version, new(new Sphere("person", []), Side: new("collection", friend.Brain + "/posts"), Main: new("person", profileKey)), null, []), []));
            if (focus != profileKey && PostBrain(focus) != friend.Brain) { continue; }
            foreach (var key in await Posts(friend))
            {
                var card = await grains.GetGrain<ISharedCardReader>(key).Read();
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
