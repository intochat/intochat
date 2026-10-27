using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class CompiledAppReviewFacts
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly CallerContext Owner = new()
    {
        AccountId = "account", BrainId = "brain", PrincipalId = "person",
        Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
    };
    private static readonly string Scope = BrainScope.Create(Owner.AccountId, Owner.BrainId).Id;

    [Fact]
    public void EvenAnEmptyPackageNeedsExplicitCodeTrust()
    {
        var store = new CompiledAppReviewStore(new Clock());
        var review = Create(store);
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize(Approval(review) with { TrustCode = false }, Scope, "counter", 0, Owner));
        Assert.Equal(Digest, store.Authorize(Approval(review), Scope, "counter", 0, Owner).Digest);
    }

    [Fact]
    public void ModelOriginatedOwnerCannotCreateACodeReview()
    {
        var store = new CompiledAppReviewStore(new Clock());
        var modelCaller = Owner with { ConversationId = "chat" };
        Assert.Throws<UnauthorizedAccessException>(() => store.Create(Scope, "counter", Digest, 0, false, new(), [], modelCaller));
        Assert.Equal(Digest, Create(store).Digest);
    }

    [Fact]
    public void ModelOriginatedOwnerCannotApproveAHumanReview()
    {
        var store = new CompiledAppReviewStore(new Clock());
        var review = Create(store);
        var approval = Approval(review);
        var modelCaller = Owner with { ConversationId = "chat" };
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize(approval, Scope, "counter", 0, modelCaller));
        Assert.Equal(Digest, store.Authorize(approval, Scope, "counter", 0, Owner).Digest);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("proxy")]
    [InlineData("person")]
    [InlineData("brain")]
    [InlineData("destination")]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("consent")]
    [InlineData("operation")]
    public void ApprovalIsBoundToItsExactReview(string changed)
    {
        var store = new CompiledAppReviewStore(new Clock());
        var review = Create(store);
        var approval = Approval(review);
        var caller = changed switch
        {
            "app" => Owner with { Kind = CallerKind.App },
            "proxy" => Owner with { StampedBy = TrustedEdge.AppProxy },
            "person" => Owner with { PrincipalId = "other" },
            _ => Owner,
        };
        if (changed == "digest") { approval = approval with { Digest = new string('b', 64) }; }
        if (changed == "consent") { approval = approval with { RequirementsConsent = "other" }; }
        if (changed == "operation") { approval = approval with { OperationId = Guid.Empty }; }
        CompiledAppReview Authorize() => store.Authorize(approval, changed == "brain" ? "other" : Scope,
            changed == "destination" ? "other" : "counter", changed == "revision" ? 1 : 0, caller);
        if (changed is "revision" or "operation") { Assert.Throws<InvalidOperationException>(Authorize); }
        else { Assert.Throws<UnauthorizedAccessException>(Authorize); }
    }

    [Fact]
    public void ReplacingAnExistingAppRequiresItsOwnAcknowledgement()
    {
        var store = new CompiledAppReviewStore(new Clock());
        var review = Create(store, replaces: true);
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize(Approval(review), Scope, "counter", 0, Owner));
        Assert.True(store.Authorize(Approval(review) with { ReplaceExisting = true }, Scope, "counter", 0, Owner).ReplacesExisting);
    }

    [Fact]
    public void ExpiredReviewCannotBeApproved()
    {
        var clock = new Clock();
        var store = new CompiledAppReviewStore(clock);
        var review = Create(store);
        clock.Now = review.ExpiresAt;
        Assert.Throws<InvalidOperationException>(() => store.Authorize(Approval(review), Scope, "counter", 0, Owner));
    }

    [Fact]
    public void ReturnedRequirementsCannotChangeServerConsent()
    {
        var store = new CompiledAppReviewStore(new Clock());
        var requirements = new AppRequirements(Capabilities: ["storage@1"]);
        var review = store.Create(Scope, "counter", Digest, 0, false, requirements, [], Owner);
        requirements.Capabilities![0] = "identity@1";
        review.Requires.Capabilities![0] = "identity@1";
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize(Approval(review), Scope, "counter", 0, Owner));
        var original = Approval(review) with { RequirementsConsent = AppConsent.Of(new(Capabilities: ["storage@1"])) };
        Assert.Equal(["storage@1"], store.Authorize(original, Scope, "counter", 0, Owner).Requires.Capabilities!);
    }

    [Fact]
    public void OneReviewCannotAuthorizeTwoDifferentOperations()
    {
        var store = new CompiledAppReviewStore(new Clock());
        var review = Create(store);
        var approval = Approval(review);
        _ = store.Authorize(approval, Scope, "counter", 0, Owner);
        _ = store.Authorize(approval, Scope, "counter", 0, Owner);
        Assert.Throws<InvalidOperationException>(() => store.Authorize(approval with { OperationId = Guid.NewGuid() }, Scope, "counter", 0, Owner));
    }

    private static CompiledAppReview Create(CompiledAppReviewStore store, bool replaces = false)
        => store.Create(Scope, "counter", Digest, 0, replaces, new(), [], Owner);

    private static CompiledAppApproval Approval(CompiledAppReview review)
        => new(review.Token, review.Digest, Guid.NewGuid(), true, AppConsent.Of(review.Requires), false);

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
