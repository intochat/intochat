using System.Security.Cryptography;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// The trust cases of compiled installation, run through the real assignment: what an owner approved is exactly what reaches the brain's
// runtime, and nothing else can.
public sealed class CompiledAppAssignmentFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, string> NoBindings = [];

    private static IBrainPrograms Programs(CompiledAppInstallFacts.Fixture fixture)
        => fixture.Brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IBrainPrograms>(BrainRuntimeKeys.For(fixture.Person.BrainId));

    private static string Key(CompiledAppInstallFacts.Fixture fixture) => IApp.Key(fixture.Person.BrainId, "counter");

    [Trait("host", "own")]
    [Fact]
    public async Task AnApprovedPackageIsAssignedByItsVerifiedAssemblyAndItsDigestIsKept()
    {
        await using var fixture = await CompiledAppInstallFacts.Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        var package = await fixture.Package();
        var review = await fixture.Installer.Prepare(package.Digest, "counter", Ct);
        var authorized = await fixture.Installer.Authorize(CompiledAppInstallFacts.Approve(review), "counter", review.ExpectedRevision, NoBindings, Ct);

        var snapshot = await fixture.Assignment.Assign(authorized, Ct);

        var assembly = await File.ReadAllBytesAsync(Path.Combine(package.DirectoryPath, authorized.Package.Descriptor.EntryAssembly), Ct);
        var program = await Programs(fixture).Describe(Key(fixture));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(assembly)), Assert.Single(program!.Behaviors).Hash);
        Assert.Equal("Counter", snapshot.Manifest.Title);
        var current = await fixture.Assignment.Current(fixture.Person.BrainId, "counter", snapshot, Ct);
        Assert.Equal(package.Digest, current!.Digest);
        Assert.True(current.DispatchReady);
        // A restart reads the same installation back from the package cache.
        var restarted = new SandboxCompiledApps(fixture.Brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>(), fixture.Installer,
            new(Path.Combine(fixture.Home, "packages", "active")));
        Assert.Equal(package.Digest, (await restarted.Current(fixture.Person.BrainId, "counter", snapshot, Ct))!.Digest);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task APackageWithNoCapabilitiesStillNeedsTheOwnersTrustBeforeAnythingIsAssigned()
    {
        await using var fixture = await CompiledAppInstallFacts.Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        var package = await fixture.Package();
        var review = await fixture.Installer.Prepare(package.Digest, "counter", Ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
            CompiledAppInstallFacts.Approve(review) with { TrustCode = false }, "counter", review.ExpectedRevision, NoBindings, Ct));

        Assert.Null(await Programs(fixture).Describe(Key(fixture)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppProxyAndAModelCannotApprove()
    {
        await using var fixture = await CompiledAppInstallFacts.Fixture.Start();
        var package = await fixture.Package();
        CompiledAppReview review;
        using (fixture.Person.AsCaller()) { review = await fixture.Installer.Prepare(package.Digest, "counter", Ct); }

        using (fixture.Person.AsApp("someapp"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
                CompiledAppInstallFacts.Approve(review), "counter", review.ExpectedRevision, NoBindings, Ct));
        }
        using (fixture.Person.AsModelImpersonatingCaller("conversation"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
                CompiledAppInstallFacts.Approve(review), "counter", review.ExpectedRevision, NoBindings, Ct));
        }
        Assert.Null(await Programs(fixture).Describe(Key(fixture)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReviewOfAnotherBrainCannotInstallHere()
    {
        await using var fixture = await CompiledAppInstallFacts.Fixture.Start();
        var other = await fixture.Brain.PersonAsync("another-owner", Ct);
        var package = await fixture.Package();
        CompiledAppReview review;
        using (other.AsCaller()) { review = await fixture.Installer.Prepare(package.Digest, "counter", Ct); }

        using (fixture.Person.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
                CompiledAppInstallFacts.Approve(review), "counter", review.ExpectedRevision, NoBindings, Ct));
        }
        Assert.Null(await Programs(fixture).Describe(Key(fixture)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AChangedDigestInvalidatesTheReviewAndExpandedRequirementsNeedFreshConsent()
    {
        await using var fixture = await CompiledAppInstallFacts.Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        var package = await fixture.Package();
        var review = await fixture.Installer.Prepare(package.Digest, "counter", Ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
            CompiledAppInstallFacts.Approve(review) with { Digest = new string('a', 64) }, "counter", review.ExpectedRevision, NoBindings, Ct));
        // The consent text names exactly what the review lists; consent given to less is no consent to more.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
            CompiledAppInstallFacts.Approve(review) with { RequirementsConsent = AppConsent.Of(new(Secrets: ["a-new-secret"])) },
            "counter", review.ExpectedRevision, NoBindings, Ct));
        Assert.Null(await Programs(fixture).Describe(Key(fixture)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReplacingAnInstalledAppNeedsANewReviewBoundToItsRevision()
    {
        await using var fixture = await CompiledAppInstallFacts.Fixture.Start();
        using var caller = fixture.Person.AsCaller();
        var package = await fixture.Package();
        var first = await fixture.Installer.Prepare(package.Digest, "counter", Ct);
        var authorized = await fixture.Installer.Authorize(CompiledAppInstallFacts.Approve(first), "counter", first.ExpectedRevision, NoBindings, Ct);
        var installed = await fixture.Assignment.Assign(authorized, Ct);

        var second = await fixture.Installer.Prepare(package.Digest, "counter", Ct);

        Assert.True(second.ReplacesExisting);
        Assert.Equal(installed.ProgramRevision, second.ExpectedRevision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Installer.Authorize(
            CompiledAppInstallFacts.Approve(second) with { ReplaceExisting = false }, "counter", second.ExpectedRevision, NoBindings, Ct));
        // The first approval cannot be used again for a new destination revision.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Assignment.Assign(authorized with { Receipt = authorized.Receipt with { ExpectedRevision = installed.ProgramRevision + 1 } }, Ct));
    }
}
