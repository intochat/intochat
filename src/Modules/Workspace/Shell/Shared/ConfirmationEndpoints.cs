using DigitalBrain.Brains;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Flutter;

// POST /brains/{id}/confirmations/{planId} {approve}: a person answers one plan of the brain's confirmation inbox. A plan id may hold
// '/' (a code block run's "cb-codeblocks/..."), so the route takes the rest of the path and a client escapes each segment. It is an inbox
// answer, not a UI action: only a direct, signed-in or presence-proved person answers (ShellPresenceGate), never an app, an
// agent or a person speaking inside a conversation. Callers: the shell (IntoChatOSUiClient.answerConfirmation) and the CLI
// (CliCommands.Answer). Responses: 200 delivered; 400 a missing answer or a plan id that is empty or longer than 128
// characters; 401 a caller who is not a direct person.
internal static class ConfirmationEndpoints
{
    public const int MaxPlanId = 128;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        BrainRouteGroups.Brain(endpoints, "/confirmations").AddEndpointFilter(ModuleRouteGuard.Guard)
            .MapPost("/{**planId}", (HttpContext http, string planId, ConfirmationAnswer input, INeuronProxies brain, CancellationToken ct)
                => Answer(planId, input, brain, BrainScope.CurrentId(), HttpCaller.Current(http), ct));
    }

    internal static async Task<IResult> Answer(string planId, ConfirmationAnswer input, INeuronProxies brain, string scope, DigitalBrain.Access.Caller? caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(brain);
        if (planId is not { Length: > 0 and <= MaxPlanId } || (input.Approve is null && input.OptionIndex is null))
        {
            throw new ArgumentException("A confirmation answer names its plan and approve or optionIndex.");
        }
        ShellPresenceGate.RequireDirectPerson(caller);
        var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(scope));
        if (input.OptionIndex is { } optionIndex)
        {
            await inbox.ResolveOption(planId, optionIndex).WaitAsync(ct);
        }
        else
        {
            await inbox.Resolve(planId, input.Approve!.Value).WaitAsync(ct);
        }
        return Results.Ok(new { delivered = true });
    }

    // Dart mirror: IntoChatOSUiClient.answerConfirmation in ui/lib/src/core/ui_client.dart.
    internal sealed record ConfirmationAnswer(bool? Approve, int? OptionIndex = null);
}
