using System.Text.Json;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.Orleans;
using DigitalBrain.Testing.Aspire;
using DigitalBrain.Testing.Neurons;

namespace DigitalBrain.Testing.Tests;

public sealed class AspireTargetFacts
{
    [Fact]
    public async Task AnAspireTargetRefusesWithModuleBecauseTheAppHostOwnsComposition()
    {
        var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => TestBrain.Create().WithModule<PulseModule>()
            .On(new AspireTarget<object>()).StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("AppHost owns composition", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAspireTargetRefusesConfigureServicesBecauseItIsInProcessOnly()
    {
        var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => TestBrain.Create().ConfigureServices(_ => { })
            .On(new AspireTarget<object>()).StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("in-process only", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOptionsOnAnUndeclaredModuleProjectsReplaceOptionsIntoThePlanConfiguration()
    {
        var target = new RecordingTarget();
        await Assert.ThrowsAsync<NotSupportedException>(() => TestBrain.Create().WithOptions<OrleansModule, OrleansOptions>(_ => { })
            .On(target).StartAsync(TestContext.Current.CancellationToken));
        var plan = Assert.IsType<TestBrainPlan>(target.Plan);
        Assert.Empty(plan.Modules);
        Assert.Equal("true", plan.Configuration[DigitalBrainConfiguration.ModuleSection("orleans") + ":ReplaceOptions"]);
    }

    [Fact]
    public async Task AnIntegrationValueWithSeparatorsTravelsInThePrivateFileVerbatim()
    {
        var value = "Host=db;Username=u;Password=p=q";
        await using var file = await PrivateTestConfiguration.CreateAsync(new Dictionary<string, string?> { [IntegrationSchema.Key("postgres", "ConnectionString")] = value }, TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(file.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(value, json.RootElement.GetProperty(IntegrationSchema.Key("postgres", "ConnectionString")).GetString());
    }

    private sealed class RecordingTarget : ITestTarget
    {
        public TestBrainPlan? Plan { get; private set; }

        public Task<ITestBrain> StartAsync(TestBrainPlan plan, CancellationToken cancellationToken)
        {
            Plan = plan;
            throw new NotSupportedException("Recorded.");
        }
    }
}
