using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Finding 6 (54-friends-merge-design.md section 4) and Finding 1 row 4: INeuronHost.InspectState is exempt from the
// access gate and serialises the WHOLE neuron state, and gRPC NeuronsGrpcService.ReadState returns exactly that state to
// any session admitted to the brain, with no per-field check. The roster once stored two PKCS#8 private keys in its state
// record; they now live sealed in the identity module (IBrainKeys). This guard reads the roster neuron's inspected state
// the way ReadState does on the silo (IBrainObserver.State -> INeuronHost.InspectState) and holds that no private key
// material is ever in it.
//
// Who is admitted: NeuronsGrpcService.ReadState calls BrainCall.Enter, which admits only a Person or Guest session that
// also passes BrainAccessFilter.Decide for that brain -- not another brain, not by itself an app/agent HTTP session.
[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsRosterKeyExposureFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
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
        Assert.Equal(JsonValueKind.Object, state!.Value.ValueKind);

        // No private key bytes are ever present in inspected roster state. The record keeps the two legacy slots so an older
        // roster can hand its pair over; a roster minted now leaves them empty.
        foreach (var field in state.Value.EnumerateObject())
        {
            if (field.Name.Contains("Pkcs8", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(JsonValueKind.Null, field.Value.ValueKind);
            }
        }
    }
}
