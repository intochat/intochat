using DigitalBrain.Brains;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Identity.Accounts;

/// <summary>
/// Produces a structured HTTP 428 Precondition Required response when an ephemeral brain attempts an operation
/// that requires claiming the workspace into durable persistent storage.
/// </summary>
public static class ClaimPromptResult
{
    public const string ClaimEndpoint = "/identity/claim";

    public static IResult FromDecision(CapabilityDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Results.Json(
            new
            {
                code = decision.Code,
                requiresClaim = true,
                capability = decision.Capability.ToString(),
                resource = decision.Resource,
                message = decision.Message,
                remainingTurns = decision.RemainingTurns,
                claimEndpoint = ClaimEndpoint,
            },
            statusCode: StatusCodes.Status428PreconditionRequired);
    }
}
