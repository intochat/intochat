using DigitalBrain.Testing;
using IntoChat.OS.Apps.Settings;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class SettingsAppContractFacts(FlagshipAppsRuntimeFixture fixture)
    : AppConcurrencyAndIsolationContract<SettingsApp, FlagshipAppsRuntimeFixture>(fixture)
{
    protected override string? ConcurrentActivationKey => "nav-desktop";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Navigation_SwitchesToDesktopSection_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        app.ShouldDisplayText("Settings").ShouldDisplayText("General");

        await app.Click("nav-desktop", ct);
        app.ShouldDisplayText("Desktop & Dock").ShouldDisplayText("OS bar indicators");
    }
}
