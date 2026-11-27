using System.Net.Http.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Directory;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Finding 1 (06-ungated-grain-calls.md): the silo access filter authorizes only INeuronHost calls that carry a
// RuntimeInvocation and app callers on other grains. A grain call that arrives with no stamped caller passes every
// filter. These facts drive the REAL in-process silo pipeline (CallerEdgeFilter + SiloAccessCallFilter) with the
// ambient caller cleared, exactly as an unstamped gateway-client call would arrive. They do NOT construct the grain
// directly and do NOT bypass the filter. Each is named for the security property that SHOULD hold, so it FAILS today
// if the gap is real and will pass once the filter refuses by default.
//
// Caveat recorded in the report: these run through a co-hosted IGrainFactory rather than a separate TCP cluster client.
// For these grain surfaces the filter decision is identical (both arrive with a null ambient caller and no CallerWire);
// the only thing a separate TCP client would add is the deployment fact that the gateway has no TLS or token, which is
// established by reading the hosting configuration, not in-process.
[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsUngatedGrainFacts(Gate20EdgeApprovalWallFacts.EdgeKernelFixture fixture)
    : IClassFixture<Gate20EdgeApprovalWallFacts.EdgeKernelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Password = "correct-horse-battery-staple";

    [Fact]
    public async Task GatewayClientWithNoCaller_CannotReadAPrincipalCredentialHash()
    {
        var host = fixture.Host;
        var principal = "victim-" + Guid.NewGuid().ToString("N")[..8];
        await RegisterAsync(host, principal);

        var factory = host.Services.GetRequiredService<IGrainFactory>();
        Orleans.Runtime.RequestContext.Clear();

        var credential = factory.GetGrain<IPrincipalCredential>(principal);

        // SHOULD hold: an unstamped grain call cannot read a password hash.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(credential.Hash);
    }

    [Fact]
    public async Task GatewayClientWithNoCaller_CannotReplaceAPrincipalCredentialHash()
    {
        var host = fixture.Host;
        var principal = "victim-" + Guid.NewGuid().ToString("N")[..8];
        await RegisterAsync(host, principal);

        var factory = host.Services.GetRequiredService<IGrainFactory>();
        Orleans.Runtime.RequestContext.Clear();

        var credential = factory.GetGrain<IPrincipalCredential>(principal);

        // SHOULD hold: an unstamped grain call cannot overwrite a password hash (account takeover).
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => credential.Rehash("$pbkdf2$forged"));
    }

    [Fact]
    public async Task GatewayClientWithNoCaller_CannotAddItselfToAnyBrainAsOwner()
    {
        var host = fixture.Host;
        Orleans.Runtime.RequestContext.Clear();

        var directory = DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<DigitalBrain.Identity.Local.Directory.IDirectory>(
            host.Services,
            DigitalBrain.Identity.Local.Directory.IDirectory.Key);

        // SHOULD hold: an unstamped call cannot grant itself membership of a brain with any role.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => directory.EnsureMember(new DigitalBrain.Capabilities.Identity.Member
            {
                AccountId = host.Scope,
                BrainId = host.Scope,
                PrincipalId = "attacker",
                DisplayName = "Attacker",
                Role = DigitalBrain.Capabilities.Identity.MemberRole.Owner,
            }));
    }

    private static async Task RegisterAsync(Gate20EdgeApprovalWallFacts.EdgeKernelHarness host, string principal)
    {
        using var res = await host.Client.PostAsJsonAsync(
            "/identity/register",
            new { principalId = principal, password = Password, displayName = principal },
            Ct);
        res.EnsureSuccessStatusCode();
    }
}
