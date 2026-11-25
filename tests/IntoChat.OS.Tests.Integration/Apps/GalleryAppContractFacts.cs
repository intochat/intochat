using DigitalBrain.Testing;
using IntoChat.OS.Apps.Gallery;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

// The UI Gallery on TestOS and on a silo: it opens on its first scenario, opens any page by its typed open command, and its one
// top-level button (the live chart's Append point) sends a typed command the Gallery handles.
public sealed class GalleryAppContractFacts(FlagshipAppsRuntimeFixture fixture)
    : AppConcurrencyAndIsolationContract<GalleryApp, FlagshipAppsRuntimeFixture>(fixture)
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Gallery_OpensOnItsFirstScenarioAndOpensAnyPage_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        app.ShouldDisplayText("Message").ShouldDisplayText("Event log");
        var gen0 = app.CurrentView.Generation;

        await app.Send(new GalleryOpen("jsonTree"), ct);
        app.ShouldDisplayText("jsonTree").ShouldDisplayText("Account state");
        Assert.True(app.CurrentView.Generation > gen0, $"Expected the generation to grow after opening jsonTree ({gen0} -> {app.CurrentView.Generation}).");
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Gallery_AppendPointButtonSendsItsTypedCommand_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        await app.Send(new GalleryOpen(GalleryPages.LiveChart), ct);
        await app.Click("append", ct);
        await app.WaitForIdleAsync(ct);
        await app.Refresh(ct);

        app.ShouldDisplayText("control.chartChanged");
    }
}
