using DigitalBrain.Framework.Compute;
using DigitalBrain.Platform.Compute;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Platform.Tests.Unit;

public sealed class ComputeReaderFacts
{
    [Fact]
    public async Task SharedBrainAppsExposeOnlyBrainSpendAndNeverEitherMembersAccountBalance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var other = await brain.PersonAsync("other", ct);
        await brain.Grains.GetGrain<IIdentityDirectory>(IdentityGrains.Directory)
            .ShareBrainAsync(brain.Owner.AccountId, brain.Owner.BrainId, other.PrincipalId, "Other", MemberRole.Member, ct);
        await brain.Grains.GetGrain<ILedgerGrain>(brain.Owner.AccountId).Credit("owner-credit", 2_000_000, "test");
        await brain.Grains.GetGrain<ILedgerGrain>(other.AccountId).Credit("other-credit", 9_000_000, "test");
        var usage = brain.Grains.GetGrain<IUsageGrain>(brain.Owner.BrainId);
        await usage.Recent(0);
        await usage.Add(new Cost
        {
            TraceId = "trace", CostId = "cost", Kind = CostKind.Model, Item = "model",
            Quantity = 1, Unit = "token", Micros = 1_250, App = "example", At = DateTimeOffset.UtcNow,
        }, "test");
        var reader = new ComputeReader(brain.Grains);
        using (brain.Owner.AsApp(brain.Owner.BrainId + "/apps/usage"))
        {
            var summary = await reader.Read(DateTimeOffset.UtcNow.AddDays(-30));
            Assert.DoesNotContain(typeof(BrainUsageSummary).GetProperties(), property => property.Name.Contains("Balance", StringComparison.Ordinal));
            Assert.Equal(1_250, summary.SpentMicros);
            Assert.Equal("example", Assert.Single(summary.Rows).Key);
        }
        using (other.AsAppIn(brain.Owner.BrainId, brain.Owner.BrainId + "/apps/usage"))
        {
            var summary = await reader.Read(DateTimeOffset.UtcNow.AddDays(-30));
            Assert.Equal(1_250, summary.SpentMicros);
            Assert.Equal("example", Assert.Single(summary.Rows).Key);
            var json = System.Text.Json.JsonSerializer.Serialize(summary);
            Assert.DoesNotContain("2000000", json, StringComparison.Ordinal);
            Assert.DoesNotContain("9000000", json, StringComparison.Ordinal);
        }
        using (other.AsCaller())
        {
            var summary = await reader.Read(DateTimeOffset.UtcNow.AddDays(-30));
            Assert.Equal(0, summary.SpentMicros);
            Assert.Empty(summary.Rows);
        }
    }
}
