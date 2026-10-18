using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Authority.Compute;

namespace IntoChat.OS.Authority.Tests.Integration;

public sealed class ComputeReaderFacts(AuthorityHost host)
{
    [Fact]
    public async Task SharedBrainAppsExposeOnlyBrainSpendAndNeverEitherMembersAccountBalance()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var owner = await host.PersonAsync("owner", ct);
        using var other = await host.PersonAsync("other", ct);
        await brain.Platform.Directory()
            .ShareBrainAsync(owner.AccountId, owner.BrainId, other.PrincipalId, "Other", MemberRole.Member, ct);
        await brain.Platform.Ledger(owner.AccountId).Credit("owner-credit", 2_000_000, "test");
        await brain.Platform.Ledger(other.AccountId).Credit("other-credit", 9_000_000, "test");
        var usage = brain.Platform.Usage(owner.BrainId);
        await usage.Recent(0);
        await usage.Add(new Cost
        {
            TraceId = "trace",
            CostId = "cost",
            Kind = CostKind.Model,
            Item = "model",
            Quantity = 1,
            Unit = "token",
            Micros = 1_250,
            App = "example",
            At = DateTimeOffset.UtcNow,
        }, "test");
        var reader = new ComputeReader(brain.Platform);
        using (owner.AsApp(owner.BrainId + "/apps/usage"))
        {
            var summary = await reader.Read(DateTimeOffset.UtcNow.AddDays(-30));
            Assert.DoesNotContain(typeof(BrainUsageSummary).GetProperties(), property => property.Name.Contains("Balance", StringComparison.Ordinal));
            Assert.Equal(1_250, summary.SpentMicros);
            Assert.Equal("example", Assert.Single(summary.Rows).Key);
        }
        using (other.AsAppIn(owner.BrainId, owner.BrainId + "/apps/usage"))
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
