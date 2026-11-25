using DigitalBrain.Testing;
using IntoChat.OS.Apps.Calculator;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class CalculatorAppContractFacts(FlagshipAppsRuntimeFixture fixture)
    : AppConcurrencyAndIsolationContract<CalculatorApp, FlagshipAppsRuntimeFixture>(fixture)
{
    protected override string? ConcurrentCommandKey => "key-0";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Keypad_ComputesAndClearsExpression_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        app.ShouldDisplayText("Calculator").ShouldDisplayText("0");

        await app.Click("key-0", ct);  // 7
        app.ShouldDisplayText("7");

        await app.Click("key-15", ct); // +
        app.ShouldDisplayText("7+");

        await app.Click("key-10", ct); // 3
        app.ShouldDisplayText("7+3");

        await app.Click("key-14", ct); // =
        app.ShouldDisplayText("10");

        await app.Click("clear-display", ct);
        app.ShouldDisplayText("0");
    }
}
