using DigitalBrain.Testing;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Settings;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class FlagshipAppsRuntimeFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } = [typeof(ShippedShellAppsModule)];
}

public sealed class CalculatorAppContractFacts : AppConcurrencyAndIsolationContract<CalculatorApp, FlagshipAppsRuntimeFixture>
{
    private readonly FlagshipAppsRuntimeFixture _fixture;

    public CalculatorAppContractFacts(FlagshipAppsRuntimeFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    protected override string? ConcurrentActivationKey => "key-0";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Keypad_ComputesAndClearsExpression_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await _fixture.Runtime(kind);
        await using var session = await AppSession.OpenAsync<CalculatorApp>(runtime, ct: ct);

        session.ShouldDisplayText("Calculator").ShouldDisplayText("0");

        await session.Click("key-0", ct);  // 7
        session.ShouldDisplayText("7");

        await session.Click("key-15", ct); // +
        session.ShouldDisplayText("7+");

        await session.Click("key-10", ct); // 3
        session.ShouldDisplayText("7+3");

        await session.Click("key-14", ct); // =
        session.ShouldDisplayText("10");

        await session.Click("clear-display", ct);
        session.ShouldDisplayText("0");
    }
}

public sealed class SettingsAppContractFacts : AppConcurrencyAndIsolationContract<SettingsApp, FlagshipAppsRuntimeFixture>
{
    private readonly FlagshipAppsRuntimeFixture _fixture;

    public SettingsAppContractFacts(FlagshipAppsRuntimeFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    protected override string? ConcurrentActivationKey => "nav-desktop";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Navigation_SwitchesToDesktopSection_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await _fixture.Runtime(kind);
        await using var session = await AppSession.OpenAsync<SettingsApp>(runtime, ct: ct);

        session.ShouldDisplayText("Settings").ShouldDisplayText("General");

        await session.Click("nav-desktop", ct);
        session.ShouldDisplayText("Desktop & Dock").ShouldDisplayText("OS bar indicators");
    }
}

public sealed class GalleryAppContractFacts : AppConcurrencyAndIsolationContract<GalleryApp, FlagshipAppsRuntimeFixture>
{
    private readonly FlagshipAppsRuntimeFixture _fixture;

    public GalleryAppContractFacts(FlagshipAppsRuntimeFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    protected override string? ConcurrentActivationKey => "nav:colors";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Navigation_SwitchesToColorsSection_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await _fixture.Runtime(kind);
        await using var session = await AppSession.OpenAsync<GalleryApp>(runtime, ct: ct);

        session.ShouldDisplayText("IntoChat UI Gallery");

        await session.Click("nav:colors", ct);
        session.ShouldDisplayText("Colors & surfaces");
    }
}
