using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Secrets;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit;

public sealed class PlatformHostingFacts
{
    [Fact]
    public async Task IdentityAnswersOwnershipMembershipAndGrantQuestionsThroughTheSdk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var identity = brain.SiloServices.GetRequiredService<IIdentity>();
        Assert.Null(identity.CurrentPrincipal);
        Assert.Throws<UntrustedCallerException>(identity.RequirePrincipal);
        using var reader = await brain.PersonAsync("reader", ct);
        Assert.True(await identity.CanAccessAsync(reader.PrincipalId, reader.AccountId, reader.BrainId, ct));
        Assert.False(await identity.CanAccessAsync(reader.PrincipalId, reader.AccountId, "another-brain", ct));
        using var app = reader.AsApp("reader-app");
        var caller = identity.CurrentPrincipal!;
        Assert.Equal((CallerKind.App, "reader-app", reader.BrainId), (caller.Kind, caller.AppId, caller.BrainId));
        Assert.Equal(reader.PrincipalId, identity.RequirePrincipal());
        var chat = caller with { ConversationId = "chat" };
        Assert.False(await identity.HasGrantAsync(chat, "person.email", ct));
        var store = brain.Grains.GetGrain<IBrainAuthority>(reader.BrainId);
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
        var authority = brain.Grains.GetGrain<IBrainAuthority>(BrainScope.Create("owner", "owner").Id);
        await authority.Initialize("owner", "owner");
        var request = new CallRequest { Caller = caller, TargetNeuron = "person", Operation = "Read", SemanticTypeIds = ["person.email"] };
        var filter = brain.SiloServices.GetRequiredService<ICallFilter>();
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
