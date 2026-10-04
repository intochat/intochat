using DigitalBrain.Apps;

namespace DigitalBrain.Modules.Apps.Tests.Workspace;

// Adoption is the Apps module's: the replacement app transfers the legacy app's storage
// ownership and retires it, idempotently, only once the replacement itself is installed.
public sealed class AppAdoptionFacts
{
    private static readonly PackageId Legacy = PackageId.Parse("intochat/hello");
    private static readonly PackageId Replacement = PackageId.Parse("os/hello");

    [Fact]
    public async Task AdoptingRetiresTheLegacyAppAndRepeatingIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PackageBrain.StartAsync(ct);
        var legacyRevision = await Publish(brain, Legacy);
        var replacementRevision = await Publish(brain, Replacement);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var scope = DigitalBrain.Kernel.Enforcement.BrainScope.CurrentId();
        var legacyApp = brain.Get<IApp>(scope + "/packages/" + Legacy);
        await legacyApp.Install(new(Guid.NewGuid(), new(Legacy, legacyRevision.Id), new Dictionary<string, string>()));
        var replacementApp = brain.Get<IApp>(scope + "/packages/" + Replacement);
        await replacementApp.Install(new(Guid.NewGuid(), new(Replacement, replacementRevision.Id), new Dictionary<string, string>()));

        await brain.Get<IAppAdoption>(scope + "/packages/" + Replacement).Adopt(Legacy);

        Assert.Equal(AppStatus.Installed, (await replacementApp.Read()).Status);
        Assert.NotEqual(AppStatus.Installed, (await legacyApp.Read()).Status);
        // Repeating after the legacy app is gone must be a quiet no-op.
        await brain.Get<IAppAdoption>(scope + "/packages/" + Replacement).Adopt(Legacy);
        Assert.Equal(AppStatus.Installed, (await replacementApp.Read()).Status);
    }

    [Fact]
    public async Task AdoptingWhenNoLegacyIsInstalledIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PackageBrain.StartAsync(ct);
        var replacementRevision = await Publish(brain, Replacement);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var scope = DigitalBrain.Kernel.Enforcement.BrainScope.CurrentId();
        var replacementApp = brain.Get<IApp>(scope + "/packages/" + Replacement);
        await replacementApp.Install(new(Guid.NewGuid(), new(Replacement, replacementRevision.Id), new Dictionary<string, string>()));

        await brain.Get<IAppAdoption>(scope + "/packages/" + Replacement).Adopt(Legacy);

        Assert.Equal(AppStatus.Installed, (await replacementApp.Read()).Status);
        Assert.Equal(AppStatus.NotInstalled, (await brain.Get<IApp>(scope + "/packages/" + Legacy).Read()).Status);
    }

    [Fact]
    public async Task AdoptionRequiresTheReplacementToBeInstalledSoTheLegacyAppIsNeverOrphaned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PackageBrain.StartAsync(ct);
        var legacyRevision = await Publish(brain, Legacy);
        Caller.As("alice");
        await brain.AuthorizeCallerAsync();
        var scope = DigitalBrain.Kernel.Enforcement.BrainScope.CurrentId();
        var legacyApp = brain.Get<IApp>(scope + "/packages/" + Legacy);
        await legacyApp.Install(new(Guid.NewGuid(), new(Legacy, legacyRevision.Id), new Dictionary<string, string>()));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => brain.Get<IAppAdoption>(scope + "/packages/" + Replacement).Adopt(Legacy));

        Assert.Equal(AppStatus.Installed, (await legacyApp.Read()).Status);
    }

    private static async Task<PackageRevision> Publish(PackageBrain brain, PackageId id)
    {
        Caller.As(id.Owner);
        await brain.AuthorizeCallerAsync();
        var package = brain.Get<IPackage>(id.ToString());
        var revision = await package.Commit(brain.Commit((await package.Read()).Head, PackageSamples.Researcher("Research"), "Research"));
        await package.Publish(new(Guid.NewGuid(), revision.Id));
        Caller.Clear();
        return revision;
    }
}
