using DigitalBrain.Access;
using IntoChat.OS.Tests;
using DigitalBrain.Capabilities;
using DigitalBrain.Testing.Conformance.Host;
using Xunit;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Files.FileSharesDoorFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Files;

// A friend is a link both people confirmed with a code. Sharing, and what a person sees under "Shared with me", follow
// from confirmed friends only. The world starts with Ada and Ben already friends.
public sealed class FriendLinksFacts
{
    [Fact]
    public async Task Redeeming_a_code_confirms_both_sides()
    {
        await using var world = await World.Start();

        ProfileState ada, ben;
        using (As(Ada)) { ada = await world.Profile(Ada).Read(); }
        using (As(Ben)) { ben = await world.Profile(Ben).Read(); }

        var hers = Assert.Single(ada.Friends!);
        var his = Assert.Single(ben.Friends!);
        Assert.Equal((Ben, Ben, true), (hers.Account, hers.BrainId, hers.Confirmed));
        Assert.Equal((Ada, Ada, true), (his.Account, his.BrainId, his.Confirmed));
    }

    [Fact]
    public async Task A_code_works_once()
    {
        await using var world = await World.Start();
        FriendCode code;
        using (As(Ada)) { code = await world.Links(Ada).Mint(); }

        using (As(Eve)) { await world.Links(Eve).Redeem(code.Code); }
        using (As("dan")) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links("dan").Redeem(code.Code)); }
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var clock = new OffsetClock();
        await using var world = await World.Start(clock);
        FriendCode code;
        using (As(Ada)) { code = await world.Links(Ada).Mint(); }

        clock.Advance(TimeSpan.FromHours(25));
        using (As(Eve)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Eve).Redeem(code.Code)); }
    }

    [Fact]
    public async Task A_wrong_or_malformed_code_is_refused()
    {
        await using var world = await World.Start();
        using (As(Ada)) { await world.Links(Ada).Mint(); }

        using var eve = As(Eve);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Eve).Redeem("ada.AAAA-AAAA-AAAA"));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Links(Eve).Redeem("not a code"));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Links(Eve).Redeem("eve.AAAA-AAAA-AAAA"));
    }

    [Fact]
    public async Task Enough_wrong_codes_withdraw_the_real_one()
    {
        await using var world = await World.Start();
        FriendCode code;
        using (As(Ada)) { code = await world.Links(Ada).Mint(); }

        using var eve = As(Eve);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var suffix = $"{attempt:00}AA";
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Eve).Redeem($"ada.AAAA-AAAA-{suffix}"));
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Eve).Redeem(code.Code));
    }

    [Fact]
    public async Task Accepted_and_Unfriended_are_refused_unless_called_through_friend_links()
    {
        await using var world = await World.Start();
        using (As(Ada)) { await world.Links(Ada).Mint(); }

        using (As(Eve))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Ada).Accepted("ANY", "eve", "Eve"));
        }

        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Ada).Unfriended());
        }
    }

    [Theory]
    [InlineData(Who.App)]
    [InlineData(Who.Agent)]
    public async Task An_app_or_an_agent_cannot_make_redeem_or_end_a_friendship(Who kind)
    {
        await using var world = await World.Start();
        FriendCode code;
        using (As(Ada)) { code = await world.Links(Ada).Mint(); }

        using var notAPerson = As(Eve, kind: kind);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Eve).Mint());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Eve).Redeem(code.Code));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Eve).Unfriend(Ada));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Links(Ada).Unfriended());
    }

    [Fact]
    public async Task A_person_cannot_confirm_a_friend_in_a_profile_directly()
    {
        await using var world = await World.Start();

        using var ada = As(Ada);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Profile(Ada).ConfirmFriend(Eve, Eve, "eve", "Eve"));
    }

    [Fact]
    public async Task Sharing_with_a_friend_who_is_not_confirmed_is_refused()
    {
        await using var world = await World.Start();

        using var ada = As(Ada);
        var typed = await world.Profile(Ada).AddFriend(Eve, "eve", "Eve");
        Assert.Contains(typed.Friends!, friend => friend is { BrainId: Eve, Confirmed: false });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Share(Eve, "/work/trip", ShareRole.Viewer));
    }

    [Fact]
    public async Task Refreshing_lists_what_confirmed_friends_share_and_drops_what_they_took_back()
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
    public async Task A_person_only_learns_of_shares_made_for_them()
    {
        await using var world = await World.Start();
        await world.ShareTrip(ShareRole.Viewer);
        await world.Befriend(Ada, Eve);

        using var eve = As(Eve);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).SharedWithCaller());
        Assert.Empty(await world.Mine(Eve).Refresh());
    }

    [Fact]
    public async Task An_archive_import_restores_friends_as_unconfirmed()
    {
        await using var world = await World.Start();

        string json;
        using (As(Ada))
        {
            var exported = await PortableBrainArchive.ExportAsync(Ada, world.Settings(Ada), world.Profile(Ada), world.Studio(Ada));
            json = PortableBrainArchive.ToJson(exported);
        }

        using (As(Eve))
        {
            var snapshot = PortableBrainArchive.FromJson(json);
            await PortableBrainArchive.ImportAsync(snapshot, world.Settings(Eve), world.Profile(Eve), world.Studio(Eve));
            var profile = await world.Profile(Eve).Read();
            var restored = Assert.Single(profile.Friends!);
            Assert.Equal(Ben, restored.BrainId);
            Assert.False(restored.Confirmed);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Eve).Share(Ben, "/work/trip", ShareRole.Viewer));
        }
    }

    [Fact]
    public async Task Unfriending_removes_the_link_on_both_sides_and_every_share_between_them()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);
        using (As(Ben)) { Assert.Single(await world.Mine(Ben).Refresh()); }

        using (As(Ada))
        {
            await world.Links(Ada).Unfriend(Ben);
            Assert.Empty((await world.Profile(Ada).Read()).Friends!);
            Assert.Empty(await world.Door(Ada).Shares());
        }

        using (As(Ben))
        {
            Assert.Empty((await world.Profile(Ben).Read()).Friends!);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
            Assert.Empty(await world.Mine(Ben).Refresh());
        }
    }

    [Fact]
    public async Task The_other_side_can_end_it_too()
    {
        await using var world = await World.Start();
        await world.ShareTrip(ShareRole.Viewer);

        using (As(Ben)) { await world.Links(Ben).Unfriend(Ada); }

        using var ada = As(Ada);
        Assert.Empty((await world.Profile(Ada).Read()).Friends!);
        Assert.Empty(await world.Door(Ada).Shares());
    }
}
