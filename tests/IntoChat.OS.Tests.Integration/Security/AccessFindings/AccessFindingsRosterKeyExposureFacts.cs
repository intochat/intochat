using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Finding 6 (54-friends-merge-design.md section 4) and Finding 1 row 4: INeuronHost.InspectState is exempt from the
// access gate and serialises the WHOLE neuron state. gRPC NeuronsGrpcService.ReadState returns exactly that state to any
// session admitted to the brain, with no per-field check. The friends roster neuron stores two PKCS#8 private keys in
// its state record. This fact reads the roster neuron's inspected state the same way ReadState does internally
// (IBrainObserver.State -> INeuronHost.InspectState) and asserts the property that SHOULD hold: private key material is
// never present in inspected roster state. It FAILS today if the keys are exposed.
//
// Who is admitted (established by reading code, not in-process): NeuronsGrpcService.ReadState calls
// BrainCall.Enter, which admits only a Person or Guest session that also passes BrainAccessFilter.Decide for that brain
// (Kernel/Platform/BrainCall.cs:33-43). So the readers are the brain's own owner person sessions and any guest the
// brain admits -- not another brain, not by itself an app/agent HTTP session.
[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsRosterKeyExposureFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Known open finding, kept out of the gate the repo's way (Explicit; run it with `-- --explicit only`). It cannot
    // reach its assertion today: the kernel's state read fails on a silo before any state (OWED.md, "state read on a silo").
    // The fix for that read is held back because it would hand these keys to every admitted session; both land with the
    // move of the keys to the identity module. Then this fact goes back to a plain [Fact].
    [Fact(Explicit = true)]
    public async Task InspectingTheFriendsRoster_DoesNotRevealPrivateSigningOrExchangeKeys()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();
        var brain = new BrainId(host.Scope);

        // Generate the roster key pair exactly as the product does, as the brain's own owner person.
        var friendsId = FriendsDefaults.Id(host.Scope);
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brain)))
        {
            var friends = DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IFriends>(host.Services, friendsId);
            await friends.Mint();
        }

        // Read the roster neuron's state the way gRPC ReadState does internally: under the person its membership decision
        // admitted (with no caller the silo refuses the read). What an admitted session would see is the whole state record.
        var observer = host.Services.GetRequiredService<IBrainObserver>();
        JsonElement? state;
        using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, brain)))
        {
            state = await observer.State(friendsId, Ct);
        }

        Assert.NotNull(state);
        var json = state!.Value.GetRawText();

        // SHOULD hold: no private key bytes are ever present in inspected roster state.
        Assert.DoesNotContain("SigningKeyPkcs8", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExchangeKeyPkcs8", json, StringComparison.OrdinalIgnoreCase);
    }
}
