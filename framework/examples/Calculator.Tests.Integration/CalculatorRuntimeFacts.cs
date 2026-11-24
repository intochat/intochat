using DigitalBrain.Examples.Calculator;
using DigitalBrain.Identity.Local;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Calculator.Tests.Integration;

public sealed class CalculatorRuntimeFixture : BothRuntimesFixture
{
    private readonly TempDirectory _home = new("db-calc-", create: false);

    protected override IReadOnlyList<Type> Modules => [typeof(CalculatorScreen)];

    protected override void ConfigureSilo(IServiceCollection services)
        => services.AddLocalIdentity(SiloNeurons.Get, _home.Path);

    protected override ValueTask DisposeCoreAsync()
    {
        _home.Dispose();
        return base.DisposeCoreAsync();
    }
}

public sealed class CalculatorRuntimeFacts(CalculatorRuntimeFixture fixture) : IClassFixture<CalculatorRuntimeFixture>
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task CalculatorScreenAgreesOnBothRuntimes(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await fixture.Open<CalculatorScreen>(kind, ct: ct);
        app.ShouldDisplayText("0");

        await app.Send(new CalculatorPress("7"), ct);
        await app.Send(new CalculatorPress("+"), ct);
        await app.Send(new CalculatorPress("5"), ct);
        await app.Send(new CalculatorPress("="), ct);
        app.ShouldDisplayText("12");

        await app.Send(new CalculatorClear(), ct);
        app.ShouldDisplayText("0");
    }
}
