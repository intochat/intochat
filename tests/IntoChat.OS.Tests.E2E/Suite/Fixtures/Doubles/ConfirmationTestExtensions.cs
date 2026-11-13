using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Testing;

namespace IntoChat.OS.Tests.E2E.Fixtures.Doubles;

/// <summary>
/// Extension methods for interacting with confirmation flows and the inbox in <see cref="TestOS"/>.
/// </summary>
public static class ConfirmationTestExtensions
{
    /// <summary>Reads all pending confirmations in the specified brain's inbox.</summary>
    public static async Task<ConfirmationRequested[]> GetPendingConfirmations(this TestOS os, BrainId brain)
    {
        var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));
        var state = await inbox.Read();
        return state.Pending;
    }

    /// <summary>Approves a pending confirmation plan in the specified brain's inbox.</summary>
    public static async Task ApprovePlan(this TestOS os, BrainId brain, string planId)
    {
        var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));
        await inbox.Resolve(planId, approve: true);
        await os.Idle();
    }

    /// <summary>Declines / rejects a pending confirmation plan in the specified brain's inbox.</summary>
    public static async Task DeclinePlan(this TestOS os, BrainId brain, string planId)
    {
        var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));
        await inbox.Resolve(planId, approve: false);
        await os.Idle();
    }

    /// <summary>Raises a confirmation request on the brain's confirmations neuron.</summary>
    public static async Task<ConfirmationResult?> RequestConfirmation(this TestOS os, BrainId brain, ConfirmationPlan plan)
    {
        var requests = os.Get<IConfirmationRequests>(IConfirmationRequests.Of(brain));
        var result = await requests.Request(plan);
        await os.Idle();
        return result;
    }

    /// <summary>Queries the authoritative outcome of a confirmation plan.</summary>
    public static async Task<ConfirmationResult?> GetConfirmationOutcome(this TestOS os, BrainId brain, string planId)
    {
        var requests = os.Get<IConfirmationRequests>(IConfirmationRequests.Of(brain));
        return await requests.Outcome(planId);
    }
}
