using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Identity.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Whether a brain holds its own provider key is asked on every model resolution, by code that runs inside a caller's turn
// and by workers that hold only a brain id. The check reads IBrainEntries.Version, a plain grain: on the real host (where
// SiloAccessCallFilter is registered) it has to carry a caller in both positions.
[Trait("Category", "AccessFindings")]
public sealed class BrainKeyAvailabilityFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheKeyCheck_ReadsTheEntriesVersion_WithNoCallerAndUnderAPerson()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var keys = host.Services.GetRequiredService<BrainKeyAvailability>();
        var edge = host.Services.GetRequiredService<EdgeAuthority>();

        // A worker with only the brain id: no caller is stamped.
        Orleans.Runtime.RequestContext.Clear();
        var (hasKey, _) = await keys.Read(host.Scope, AiProvider.OpenAI, Ct);
        Assert.False(hasKey);
        Assert.Null(edge.Current);

        // Inside a person's request the stamped caller stays the person.
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(host.Scope))))
        {
            Assert.False((await keys.Read(host.Scope, AiProvider.OpenAI, Ct)).HasKey);
            Assert.IsType<Actor.Person>(edge.Current!.Actor);
        }
    }
}
