using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Compute;

namespace DigitalBrain.Modules.Flutter.Tests.Compute;

public sealed class ComputeFacts
{
    [Fact]
    public async Task AnAppReadsTheSameBrainUsageAsItsOwnerWithoutAccountBalance()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        var scope = brain.Owner.BrainId;
        var compute = brain.Get<ICompute>(scope + "/compute");
        ComputeState owner;
        using (brain.Owner.AsCaller()) { owner = await compute.Read(); }
        using (brain.Owner.AsApp(scope + "/apps/usage"))
        {
            var app = await compute.Read();
            Assert.DoesNotContain(typeof(ComputeState).GetProperties(), property => property.Name.Contains("Balance", StringComparison.Ordinal));
            Assert.Equal(owner.SpentMicros, app.SpentMicros);
            Assert.Equal(owner.Rows, app.Rows);
            Assert.InRange(app.Since, DateTimeOffset.UtcNow.AddDays(-30).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(-30).AddMinutes(1));
        }
    }

    [Fact]
    public async Task UsageCannotBeReadThroughAnArbitraryKeyWithinTheBrain()
    {
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(TestContext.Current.CancellationToken);
        using var caller = brain.Owner.AsCaller();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Get<ICompute>(brain.Owner.BrainId + "/another-account/compute").Read());
    }
}
