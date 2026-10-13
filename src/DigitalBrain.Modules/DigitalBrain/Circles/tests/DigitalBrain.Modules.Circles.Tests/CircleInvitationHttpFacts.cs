using System.Net;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using static DigitalBrain.Circles.Tests.CircleBrainRooms;

namespace DigitalBrain.Circles.Tests;

public sealed class CircleInvitationHttpFacts
{
    [Fact]
    public async Task OnlyAnInvitedPersonCanAcceptBeforeTheyAreACircleMember()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var bob = await brain.PersonAsync("bob", ct);
        var outsider = await brain.PersonAsync("outsider", ct);
        var room = await Create(brain, alice);
        await alice.In(room, () => Circle(brain, room).Invite(bob.AccountId));
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        Assert.Null(await identity.Membership(room, bob.PrincipalId));
        using var anonymous = await brain.HttpClient.PostAsync($"/circle-invitations/{room}/accept", null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var refused = await outsider.Http.PostAsync($"/circle-invitations/{room}/accept", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var accepted = await bob.Http.PostAsync($"/circle-invitations/{room}/accept", null, ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotNull(await identity.Membership(room, bob.PrincipalId));
        Assert.Contains(bob.AccountId, (await alice.In(room, () => Circle(brain, room).State())).Members);
    }
}
