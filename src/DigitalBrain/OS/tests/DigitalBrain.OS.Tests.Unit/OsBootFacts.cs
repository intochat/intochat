using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.OS;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsBootFacts
{
    [Fact(Timeout = 60_000)]
    public async Task EstablishingABrainInstallsTheOsAppsAndReactivationUpgradesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var package = PackageId.Create("os", "hello");
        await using var brain = await ModuleTest.Create()
            .WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddOperatingSystemBoot(os => os.Apps.Add(new(package))))
            .StartAsync(ct);

        Stamp("os");
        await brain.AuthorizeCallerAsync();
        var source = brain.Get<IPackage>(package.ToString());
        var content = new PackageContent(new("Hello", "Shows hello world", [new("open", "Open")], [], Runtime: "prompt"), "");
        var first = await source.Commit(new(Guid.NewGuid(), null, content, "Initial"));
        await source.Publish(new(Guid.NewGuid(), first.Id));

        var scope = BrainScope.Create("acct", "home").Id;
        CallerContextStamper.Stamp(new()
        {
            PrincipalId = "acct",
            AccountId = "acct",
            BrainId = "home",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        await brain.AuthorizeCallerAsync();
        var neuron = brain.Get<IBrain>(scope);
        await neuron.Establish(new("home", "acct"));
        var app = brain.Get<IApp>(scope + "/packages/" + package);
        var installed = await WaitForAsync(app, snapshot => snapshot.Status == AppStatus.Installed, ct);
        Assert.Equal(first.Id, installed.Revision?.Revision);

        Stamp("os");
        var next = await source.Commit(new(Guid.NewGuid(), first.Id,
            content with { Manifest = content.Manifest with { Description = "Changed" } }, "Next"));
        await source.Publish(new(Guid.NewGuid(), next.Id));
        CallerContextStamper.Stamp(new()
        {
            PrincipalId = "acct",
            AccountId = "acct",
            BrainId = "home",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        await brain.DeactivateAsync(neuron, ct);
        await neuron.Read();
        var upgraded = await WaitForAsync(app, snapshot => snapshot.Revision?.Revision == next.Id, ct);
        Assert.Equal(AppStatus.Installed, upgraded.Status);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnUnpublishedOsAppIsSkippedAndPickedUpOnTheNextActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var hello = PackageId.Create("os", "hello");
        var later = PackageId.Create("os", "later");
        await using var brain = await ModuleTest.Create()
            .WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddOperatingSystemBoot(os =>
            {
                os.Apps.Add(new(hello));
                os.Apps.Add(new(later));
            }))
            .StartAsync(ct);

        Stamp("os");
        await brain.AuthorizeCallerAsync();
        var content = new PackageContent(new("Hello", "Shows hello world", [new("open", "Open")], [], Runtime: "prompt"), "");
        var published = await brain.Get<IPackage>(hello.ToString()).Commit(new(Guid.NewGuid(), null, content, "Initial"));
        await brain.Get<IPackage>(hello.ToString()).Publish(new(Guid.NewGuid(), published.Id));

        var scope = BrainScope.Create("acct", "home").Id;
        StampBrainOwner();
        await brain.AuthorizeCallerAsync();
        var neuron = brain.Get<IBrain>(scope);
        await neuron.Establish(new("home", "acct"));
        await WaitForAsync(brain.Get<IApp>(scope + "/packages/" + hello), snapshot => snapshot.Status == AppStatus.Installed, ct);
        Assert.Equal(AppStatus.NotInstalled, (await brain.Get<IApp>(scope + "/packages/" + later).Read()).Status);

        Stamp("os");
        var laterRevision = await brain.Get<IPackage>(later.ToString()).Commit(new(Guid.NewGuid(), null, content, "Initial"));
        await brain.Get<IPackage>(later.ToString()).Publish(new(Guid.NewGuid(), laterRevision.Id));
        StampBrainOwner();
        await brain.DeactivateAsync(neuron, ct);
        await neuron.Read();
        await WaitForAsync(brain.Get<IApp>(scope + "/packages/" + later), snapshot => snapshot.Status == AppStatus.Installed, ct);
    }

    private static void StampBrainOwner() => CallerContextStamper.Stamp(new()
    {
        PrincipalId = "acct",
        AccountId = "acct",
        BrainId = "home",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    // The boot behavior reacts to a signal, so the facts wait for the state it converges on.
    private static async Task<AppSnapshot> WaitForAsync(IApp app, Func<AppSnapshot, bool> done, CancellationToken ct)
    {
        var snapshot = await app.Read();
        var started = TimeProvider.System.GetTimestamp();
        while (!done(snapshot) && TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100, ct);
            snapshot = await app.Read();
        }
        Assert.True(done(snapshot), $"The app did not converge; status {snapshot.Status}, revision {snapshot.Revision?.Revision}.");
        return snapshot;
    }

    private static void Stamp(string principal) => CallerContextStamper.Stamp(new()
    {
        PrincipalId = principal,
        AccountId = "account-" + principal,
        BrainId = "workspace-" + principal,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });
}
