using System.Text;
using DigitalBrain.Components;
using IntoChat.OS.Modules.Identity.Contracts;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Files.FileSharesDoorFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Files;

// The roster does not call the door. It states that a friend was removed; the door drops that person's shares when it
// hears it, and until then asks the roster on every visitor call. The world starts with Ada and Ben already friends.
public sealed class FriendRemovalDoorFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("from ben");

    [Fact]
    public async Task AFriendKeepsAccessWhenAnotherFriendIsRemoved()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);
        await world.Befriend(Ada, Eve);

        using (As(Ada)) { await world.Friends(Ada).Unfriend(new AccountId(Eve)); }

        using (As(Ben))
        {
            Assert.Equal("hello", Encoding.UTF8.GetString((await world.Mine(Ben).Read(Ada, id, "day1/a.txt"))!));
            Assert.NotEmpty(await world.Mine(Ben).List(Ada, id, ""));
            Assert.Equal(Ben, (await world.Mine(Ben).Write(Ada, id, "day1/b.txt", Bytes)).ModifiedBy);
            Assert.Single(await world.Mine(Ben).Refresh());
        }
    }

    [Fact]
    public async Task APersonWhoIsNotAFriendIsRefused()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);

        using (As(Eve))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Eve).Read(Ada, id, "day1/a.txt"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Eve).List(Ada, id, ""));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Eve).Write(Ada, id, "day1/b.txt", Bytes));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Eve).Add(Ada, id));
        }
        using (As(Ada)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Share(Eve, "/work/trip", ShareRole.Viewer)); }
    }

    // Nothing is waited for between the removal and the visit.
    [Fact]
    public async Task ARemovedFriendIsRefusedOnTheVeryNextCall()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);
        using (As(Ben)) { Assert.NotNull(await world.Mine(Ben).Read(Ada, id, "day1/a.txt")); }

        using (As(Ada)) { await world.Friends(Ada).Unfriend(new AccountId(Ben)); }

        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).List(Ada, id, ""));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Write(Ada, id, "day1/b.txt", Bytes));
        }
    }

    [Fact]
    public async Task AFriendWhoLeavesIsRefusedOnTheVeryNextCall()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);

        using (As(Ben))
        {
            await world.Friends(Ben).Unfriend(new AccountId(Ada));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Write(Ada, id, "day1/b.txt", Bytes));
        }
    }

    // What the door sees between the removal and the fact: the roster no longer lists Ben, the share and the offer still
    // stand, and no fact was published. Only the door's own question to the roster can refuse him.
    [Fact]
    public async Task TheDoorRefusesAFormerFriendWhileTheirShareAndOfferStillStand()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);
        using (As(Ben)) { Assert.NotNull(await world.Mine(Ben).Read(Ada, id, "day1/a.txt")); }

        await world.ForgetFriendsWithoutSayingSo(Ada);

        using (As(Ada)) { Assert.Empty(await world.Friends(Ada).List()); }
        Assert.Equal(Role.Editor, await world.OfferTo(Ada, FileSharesDefaults.Id(Ada), Ben));
        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).List(Ada, id, ""));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Write(Ada, id, "day1/b.txt", Bytes));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Add(Ada, id));
            Assert.Empty(await world.Mine(Ben).Refresh());
        }
        // The visitor was refused while the share and the offer stood: the door has heard nothing.
        using (As(Ada)) { Assert.Equal(id, Assert.Single(await world.Door(Ada).Shares()).Id); }
        Assert.Equal(Role.Editor, await world.OfferTo(Ada, FileSharesDefaults.Id(Ada), Ben));
        Assert.Empty(await world.Runtime.Signals<FriendRemoved>(FriendsDefaults.Id(Ada), Ct));
    }

    [Fact]
    public async Task RemovingAFriendDropsTheirSharesAndKeepsTheOthers()
    {
        await using var world = await World.Start();
        await world.ShareTrip(ShareRole.Viewer);
        await world.Befriend(Ada, Eve);
        string kept;
        using (As(Ada)) { kept = (await world.Door(Ada).Share(Eve, "/work/private", ShareRole.Viewer)).Id; }

        using (As(Ada)) { await world.Friends(Ada).Unfriend(new AccountId(Ben)); }

        await World.Eventually(async () =>
        {
            using (As(Ada)) { return (await world.Door(Ada).Shares()).Select(share => share.Id).SequenceEqual([kept]); }
        }, "only the share of the friend who stayed is left");
    }

    [Fact]
    public async Task AShareDoesNotComeBackWhenTheFriendshipDoes()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);
        using (As(Ada)) { await world.Friends(Ada).Unfriend(new AccountId(Ben)); }
        await world.NoSharesLeft(Ada);

        await world.Befriend(Ada, Ben);

        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
            Assert.Empty(await world.Mine(Ben).Refresh());
        }
        using (As(Ada)) { Assert.Empty(await world.Door(Ada).Shares()); }
    }

    // A door that was asleep when the friend was removed and never heard the fact holds shares for a person who is no
    // longer a friend. It drops them when it wakes, so they cannot come back with a later friendship.
    [Fact]
    public async Task ADoorThatNeverHeardOfARemovalDropsTheSharesWhenItWakes()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);
        await world.ForgetFriendsWithoutSayingSo(Ada);

        await world.Runtime.Deactivate(FileSharesDefaults.Id(Ada));

        using (As(Ada)) { Assert.Empty(await world.Door(Ada).Shares()); }
        await world.Befriend(Ada, Ben);
        using (As(Ben)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt")); }
    }

    [Fact]
    public async Task TheRosterPublishesEveryFriendAddedAndRemovedOnBothSides()
    {
        await using var world = await World.Start();

        // Ada made the code and Ben redeemed it.
        Assert.Equal([new FriendAdded(new AccountId(Ben))], await world.Runtime.Signals<FriendAdded>(FriendsDefaults.Id(Ada), Ct));
        Assert.Equal([new FriendAdded(new AccountId(Ada))], await world.Runtime.Signals<FriendAdded>(FriendsDefaults.Id(Ben), Ct));

        using (As(Ada)) { await world.Friends(Ada).Unfriend(new AccountId(Ben)); }

        Assert.Equal([new FriendRemoved(new AccountId(Ben))], await world.Runtime.Signals<FriendRemoved>(FriendsDefaults.Id(Ada), Ct));
        Assert.Equal([new FriendRemoved(new AccountId(Ada))], await world.Runtime.Signals<FriendRemoved>(FriendsDefaults.Id(Ben), Ct));

        // Removing someone who is not a friend states nothing.
        using (As(Ada)) { await world.Friends(Ada).Unfriend(new AccountId(Ben)); }
        Assert.Single(await world.Runtime.Signals<FriendRemoved>(FriendsDefaults.Id(Ada), Ct));
    }
}
