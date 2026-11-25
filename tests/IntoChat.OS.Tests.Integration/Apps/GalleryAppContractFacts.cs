using DigitalBrain.Testing;
using IntoChat.OS.Apps.Gallery;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class GalleryAppContractFacts(FlagshipAppsRuntimeFixture fixture)
    : AppConcurrencyAndIsolationContract<GalleryApp, FlagshipAppsRuntimeFixture>(fixture)
{
    protected override string? ConcurrentCommandKey => "nav:colors";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Navigation_SwitchesToColorsSection_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        app.ShouldDisplayText("IntoChat UI Gallery");

        await app.Click("nav:colors", ct);
        app.ShouldDisplayText("Colors & surfaces");
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Gallery_NavigatesMultipleSections_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        app.ShouldDisplayText("IntoChat UI Gallery")
           .ShouldDisplayText("Welcome to IntoChat UI Gallery");
        var gen0 = app.CurrentView.Generation;

        await app.Click("nav:colors", ct);
        app.ShouldDisplayText("Colors & Surfaces")
           .ShouldDisplayText("Semantic Status Palette");
        var gen1 = app.CurrentView.Generation;
        Assert.True(gen1 > gen0, $"Expected generation to increment after nav:colors ({gen0} -> {gen1}).");

        await app.Click("nav:typography", ct);
        app.ShouldDisplayText("Type Scale")
           .ShouldDisplayText("Tabular numerals (tnum)");
        var gen2 = app.CurrentView.Generation;
        Assert.True(gen2 > gen1, $"Expected generation to increment after nav:typography ({gen1} -> {gen2}).");

        await app.Click("nav:spacing", ct);
        app.ShouldDisplayText("Spacing, Padding & Layout")
           .ShouldDisplayText("Space.Xs (4px)");
        var gen3 = app.CurrentView.Generation;
        Assert.True(gen3 > gen2, $"Expected generation to increment after nav:spacing ({gen2} -> {gen3}).");

        await app.Click("nav:buttons", ct);
        app.ShouldDisplayText("Buttons & Actions")
           .ShouldDisplayText("Guarded Disabled Button");
        var gen4 = app.CurrentView.Generation;
        Assert.True(gen4 > gen3, $"Expected generation to increment after nav:buttons ({gen3} -> {gen4}).");

        await app.Click("btn:primary", ct);
        app.ShouldDisplayText("Buttons & Actions")
           .ShouldDisplayText("Primary");
        var gen5 = app.CurrentView.Generation;
        Assert.True(gen5 > gen4, $"Expected generation to increment after btn:primary ({gen4} -> {gen5}).");
    }
}
