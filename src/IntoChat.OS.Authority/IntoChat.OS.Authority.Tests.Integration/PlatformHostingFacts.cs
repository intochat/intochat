using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Sdk.Platform.Identity;
using IntoChat.OS.Sdk.Platform.Integrations;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration;

public sealed class PlatformHostingFacts(AuthorityHost host)
{
    [Fact]
    public async Task IdentityAnswersOwnershipMembershipAndGrantQuestionsThroughTheSdk()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var identity = brain.Services.GetRequiredService<IIdentity>();
        Assert.Null(identity.CurrentPrincipal);
        Assert.Throws<UntrustedCallerException>(identity.RequirePrincipal);
        using var reader = await host.PersonAsync("reader", ct);
        Assert.True(await identity.CanAccessAsync(reader.PrincipalId, reader.AccountId, reader.BrainId, ct));
        Assert.False(await identity.CanAccessAsync(reader.PrincipalId, reader.AccountId, "another-brain", ct));
        using var app = reader.AsApp("reader-app");
        var caller = identity.CurrentPrincipal!;
        Assert.Equal((CallerKind.App, "reader-app", reader.BrainId), (caller.Kind, caller.AppId, caller.BrainId));
        // Phase F: an app call is the app's own principal, acting for the person.
        Assert.Equal(AppPrincipal.Id("reader-app"), identity.RequirePrincipal());
        Assert.Equal(reader.PrincipalId, caller.PersonId);
        var chat = caller with { ConversationId = "chat" };
        Assert.False(await identity.HasGrantAsync(chat, "person.email", ct));
        var store = brain.Platform.BrainAuthority(reader.BrainId);
        await store.Grant(new Grant
        {
            AppId = "reader-app",
            WorkspaceId = caller.BrainId,
            SemanticTypeId = "person.email",
            Mode = GrantMode.ThisChat,
            ConversationId = "chat",
        });
        Assert.True(await identity.HasGrantAsync(chat, "person.email", ct));
        Assert.False(await identity.HasGrantAsync(chat with { ConversationId = "other" }, "person.email", ct));
        Assert.Single(await store.ListGrants());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABrainWithoutModulesRequiresAGrantBeforeAnAppCanRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var caller = new CallerContext
        {
            PrincipalId = "owner",
            AccountId = "owner",
            BrainId = "owner",
            AppId = "reader",
            Kind = CallerKind.App,
            StampedBy = TrustedEdge.AppProxy,
        };
        var authority = brain.Platform.BrainAuthority(BrainScope.Create("owner", "owner").Id);
        await authority.Initialize("owner", "owner");
        var request = new CallRequest { Caller = caller, TargetNeuron = "person", Operation = "Read", SemanticTypeIds = ["person.email"] };
        var filter = brain.Services.GetRequiredService<ICallFilter>();
        Assert.Equal(CallDenial.MissingGrant, (await filter.AuthorizeAsync(request, ct)).Denial);
        await authority.Grant(new Grant
        {
            AppId = "reader",
            WorkspaceId = "owner",
            SemanticTypeId = "person.email",
            Mode = GrantMode.Once,
        });
        Assert.True((await filter.AuthorizeAsync(request, ct)).Allowed);
        Assert.Equal(CallDenial.MissingGrant, (await filter.AuthorizeAsync(request, ct)).Denial);
    }
}
