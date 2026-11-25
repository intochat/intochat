using DigitalBrain.Testing.Conformance.Host;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Files.FileSharesDoorFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Files;

// A friend is a link both people confirmed with a code. Sharing, and what a person sees under "Shared with me", follow
// from confirmed friends in IFriends only. The world starts with Ada and Ben already friends.
public sealed class FriendsFacts
{
    [Fact]
    public async Task RedeemingACodeConfirmsBothSides()
    {
        await using var world = await World.Start();

        Friend[] ada, ben;
        using (As(Ada)) { ada = await world.Friends(Ada).List(); }
        using (As(Ben)) { ben = await world.Friends(Ben).List(); }

        Assert.Equal(new AccountId(Ben), Assert.Single(ada).Account);
        Assert.Equal(new AccountId(Ada), Assert.Single(ben).Account);
    }

    [Fact]
    public async Task ACodeWorksOnce()
    {
        await using var world = await World.Start();
        Code code;
        using (As(Ada)) { code = await world.Friends(Ada).Mint(); }

        using (As(Eve)) { await world.Friends(Eve).Redeem(code); }
        using (As("dan")) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends("dan").Redeem(code)); }
    }

    [Fact]
    public async Task AnExpiredCodeIsRefused()
    {
        var clock = new OffsetClock();
        await using var world = await World.Start(clock);
        Code code;
        using (As(Ada)) { code = await world.Friends(Ada).Mint(); }

        clock.Advance(TimeSpan.FromHours(25));
        using (As(Eve)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).Redeem(code)); }
    }

    [Fact]
    public async Task MissesLockTheRedeemerOut()
    {
        await using var world = await World.Start();
        Code real;
        using (As(Ada)) { real = await world.Friends(Ada).Mint(); }

        using var eve = As(Eve);
        for (var i = 0; i < Code.MaxMisses; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).Redeem(Code.New()));
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).Redeem(real));
    }

    [Fact]
    public async Task ConfirmedProfileFriendsAreImportedOnce()
    {
        await using var world = await World.Start(
            seedProfileFriends:
                """[{"brainId":"zoe","handle":"zoe","displayName":"Zoe","scope":"friends","account":"zoe","confirmed":true},{"brainId":"tom","handle":"tom","displayName":"Tom","scope":"friends","account":"","confirmed":false}]""",
            forAccount: "kim");

        using var kim = As("kim");
        Assert.Equal(["zoe"], (await world.Friends("kim").List()).Select(f => f.Account.Value));
        Assert.Equal(["zoe"], (await world.Friends("kim").List()).Select(f => f.Account.Value));
    }

    [Fact]
    public async Task AcceptedAndUnfriendedAreRefusedUnlessCalledThroughFriends()
    {
        await using var world = await World.Start();
        using (As(Ada)) { await world.Friends(Ada).Mint(); }

        using (As(Eve))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ada).Accepted("ANY", "Eve"));
        }

        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ada).Unfriended());
        }
    }

    [Fact]
    public async Task OnlyTheOwnerAsAPersonMintsAndRedeems()
    {
        await using var world = await World.Start();
        Code code;
        using (As(Ada)) { code = await world.Friends(Ada).Mint(); }

        using (As(Ada, kind: Who.App))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ada).Mint());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ada).Redeem(code));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ada).Unfriend(new AccountId(Ben)));
        }

        using (As(Eve, kind: Who.Agent))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).Mint());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).Redeem(code));
        }

        using (As(Ben, brain: Ada))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ada).Mint());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ada).Redeem(code));
        }
    }

    [Fact]
    public async Task RefreshingListsWhatConfirmedFriendsShareAndDropsWhatTheyTookBack()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);

        using (As(Ben))
        {
            var pointer = Assert.Single(await world.Mine(Ben).Refresh());
            Assert.Equal(new SharedPointer(Ada, Ada, id, "/work/trip", IsFolder: true, ShareRole.Editor), pointer);
        }

        using (As(Ada)) { await world.Door(Ada).Unshare(id); }
        using (As(Ben)) { Assert.Empty(await world.Mine(Ben).Refresh()); }
    }

    [Fact]
    public async Task APersonOnlyLearnsOfSharesMadeForThem()
    {
        await using var world = await World.Start();
        await world.ShareTrip(ShareRole.Viewer);
        await world.Befriend(Ada, Eve);

        using var eve = As(Eve);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).SharedWithCaller());
        Assert.Empty(await world.Mine(Eve).Refresh());
    }

    [Fact]
    public async Task UnfriendingRemovesBothSidesAndTheirShares()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);
        using (As(Ben)) { Assert.Single(await world.Mine(Ben).Refresh()); }

        using (As(Ada))
        {
            await world.Friends(Ada).Unfriend(new AccountId(Ben));
            Assert.Empty(await world.Friends(Ada).List());
        }
        // The door drops the shares when it hears of the removal; the visitor is refused before that (FriendRemovalDoorFacts).
        await world.NoSharesLeft(Ada);

        using (As(Ben))
        {
            Assert.Empty(await world.Friends(Ben).List());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
            Assert.Empty(await world.Mine(Ben).Refresh());
        }
    }

    [Fact]
    public async Task TheOtherSideCanEndItToo()
    {
        await using var world = await World.Start();
        await world.ShareTrip(ShareRole.Viewer);

        using (As(Ben)) { await world.Friends(Ben).Unfriend(new AccountId(Ada)); }

        using (As(Ada)) { Assert.Empty(await world.Friends(Ada).List()); }
        await world.NoSharesLeft(Ada);
    }

    [Fact]
    public async Task RedeemingACodeExchangesBrainPublicKeysOnBothSides()
    {
        await using var world = await World.Start();

        Code code;
        using (As(Ada)) { code = await world.Friends(Ada).Mint(); }
        using (As(Eve)) { await world.Friends(Eve).Redeem(code); }

        var ct = TestContext.Current.CancellationToken;
        DigitalBrain.Invocation.BrainPublicKeys adaKeys, adaKeysAgain, eveKeys;
        Friend[] adaFriends, eveFriends;
        using (As(Ada))
        {
            adaKeys = await world.Friends(Ada).PublicKeys(ct);
            adaKeysAgain = await world.Friends(Ada).PublicKeys(ct);
            adaFriends = await world.Friends(Ada).List();
        }
        using (As(Eve))
        {
            eveKeys = await world.Friends(Eve).PublicKeys(ct);
            eveFriends = await world.Friends(Eve).List();
        }

        Assert.NotEmpty(adaKeys.SigningKey);
        Assert.NotEmpty(adaKeys.ExchangeKey);
        Assert.Equal(adaKeys.SigningKey, adaKeysAgain.SigningKey);
        Assert.Equal(adaKeys.ExchangeKey, adaKeysAgain.ExchangeKey);
        Assert.NotEmpty(eveKeys.SigningKey);
        Assert.NotEmpty(eveKeys.ExchangeKey);
        Assert.NotEqual(adaKeys.SigningKey, eveKeys.SigningKey);

        var eveOnAda = Assert.Single(adaFriends, f => f.Account == new AccountId(Eve));
        Assert.NotNull(eveOnAda.PublicKeys);
        Assert.Equal(eveKeys.SigningKey, eveOnAda.PublicKeys.SigningKey);
        Assert.Equal(eveKeys.ExchangeKey, eveOnAda.PublicKeys.ExchangeKey);

        var adaOnEve = Assert.Single(eveFriends, f => f.Account == new AccountId(Ada));
        Assert.NotNull(adaOnEve.PublicKeys);
        Assert.Equal(adaKeys.SigningKey, adaOnEve.PublicKeys.SigningKey);
        Assert.Equal(adaKeys.ExchangeKey, adaOnEve.PublicKeys.ExchangeKey);
    }
}
