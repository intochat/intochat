using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class CompiledAppDevelopmentFacts
{
    private static readonly CallerContext Owner = new() { AccountId = "account", BrainId = "brain", PrincipalId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };

    [Fact]
    public void ExpiredLeaseCannotReloadOrRenew()
    {
        var clock = new Clock();
        var leases = new CompiledAppDevelopmentLeases(clock);
        var lease = leases.Create("counter", "/project.csproj", "package", new(), [], Owner);
        clock.Now = lease.ExpiresAt;
        Assert.Throws<UnauthorizedAccessException>(() => leases.Require(lease.Token, lease.Project, Owner));
        Assert.Throws<UnauthorizedAccessException>(() => leases.Renew(lease.Token, lease.Project, Owner));
    }

    [Fact]
    public void StopRevokesSessionWithoutDeletingData()
    {
        var leases = new CompiledAppDevelopmentLeases(new Clock());
        var lease = leases.Create("counter", "/project.csproj", "package", new(), [], Owner);
        leases.Revoke(lease.Token, lease.Project, Owner);
        Assert.Throws<UnauthorizedAccessException>(() => leases.Require(lease.Token, lease.Project, Owner));
    }

    [Fact]
    public void RequirementChangePausesReload()
    {
        var leases = new CompiledAppDevelopmentLeases(new Clock());
        var requirements = new AppRequirements(Capabilities: ["storage@1"]);
        var adapters = new[] { "storage" };
        var lease = leases.Create("counter", "/project.csproj", "package", requirements, adapters, Owner);
        requirements.Capabilities![0] = "identity@1";
        adapters[0] = "identity";
        var entry = leases.Require(lease.Token, lease.Project, Owner);
        Assert.True(entry.Allows("package", new(Capabilities: ["storage@1"]), ["storage"]));
        Assert.False(entry.Allows("package", new(Capabilities: ["identity@1"]), ["storage"]));
        Assert.False(entry.Allows("package", new(), ["identity"]));
        Assert.False(entry.Allows("other", new(), []));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("brain")]
    [InlineData("principal")]
    [InlineData("account")]
    public void LeaseCannotMoveToAnotherOwnerOrProject(string changed)
    {
        var leases = new CompiledAppDevelopmentLeases(new Clock());
        var lease = leases.Create("counter", "/project.csproj", "package", new(), [], Owner);
        var owner = changed switch { "brain" => Owner with { BrainId = "other" }, "principal" => Owner with { PrincipalId = "other" }, "account" => Owner with { AccountId = "other" }, _ => Owner };
        Assert.Throws<UnauthorizedAccessException>(() => leases.Require(lease.Token, changed == "project" ? "/other.csproj" : lease.Project, owner));
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
