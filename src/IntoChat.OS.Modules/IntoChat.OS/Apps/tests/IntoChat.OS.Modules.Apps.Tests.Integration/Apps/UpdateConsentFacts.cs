using System.Net;
using System.Net.Http.Json;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using static IntoChat.OS.Apps.Tests.Apps.UnifiedInstallFacts;

namespace IntoChat.OS.Apps.Tests.Apps;

// An update that asks for more grants only what it added, and the update route answers what that is.
public sealed class UpdateConsentFacts
{
    private static readonly AppRequirements First = new(["person.email"], ["mailbox"]);

    [Trait("host", "own")]
    [Fact]
    public async Task UpdateWithConsentGrantsOnlyTheAdded()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("intochat", Ct);
        var source = await Publish(brain, publisher, First);
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        Assert.Equal(InstallStatus.Ok, (await Installer(brain).Install(Latest(AppConsent.Of(First)), Ct)).Status);
        // The person withdrew a type they had accepted; consenting to the update does not hand it back.
        Assert.True(CallerContextStamper.TryGet(out var caller));
        await Identity(brain).RevokeAsOwner(caller!, IApp.Key(alice.BrainId, "echo"), "person.email", GrantMode.Always);
        await Republish(publisher, source, Needs);

        Assert.Equal(InstallStatus.Ok, (await Installer(brain).Update(new("echo", AppConsent.Of(Needs)), Ct)).Status);

        Assert.Equal(["person.birthDate"], await Granted(brain, alice));
        Assert.Equal(Needs, (await Copy(brain, alice).Read()).Origin!.Consented);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdateRouteAnswersTheAddedWith409()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("intochat", Ct);
        var source = await Publish(brain, publisher, First);
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller()) { await Installer(brain).Install(Latest(AppConsent.Of(First)), Ct); }
        var installed = (await Copy(brain, alice).Read()).Version;
        await Republish(publisher, source, Needs);
        var update = $"/brains/{alice.BrainId}/apps/echo/update";

        using var asked = await alice.Http.PostAsJsonAsync(update, new { consent = AppConsent.Of(First) }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var added = (await Body(asked)).GetProperty("added");
        Assert.Equal(["person.birthDate"], added.GetProperty("semanticTypes").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(added.GetProperty("connectors").EnumerateArray());
        Assert.Equal(installed, (await Copy(brain, alice).Read()).Version);

        using var consented = await alice.Http.PostAsJsonAsync(update, new { consent = AppConsent.Of(Needs) }, Ct);
        Assert.Equal(HttpStatusCode.OK, consented.StatusCode);
        Assert.NotEqual(installed, (await Copy(brain, alice).Read()).Version);
    }
}
