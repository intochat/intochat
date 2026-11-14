using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;

namespace IntoChat.OS.Modules.Flutter;

/// <summary>
/// Edge presence helper for OS shell UI routes: ensures direct person-gated actions (such as confirmation
/// resolutions) carry an authenticated or presence-proved <see cref="CallerKind.User"/> stamp rather than an
/// unproved local app or MCP agent stamp.
/// </summary>
internal static class ShellPresenceGate
{
    public static void RequireDirectPerson(bool requireAmbientStamp = false)
    {
        if (!RuntimeCallerContext.TryGet(out var caller))
        {
            if (requireAmbientStamp)
            {
                throw new UnauthorizedAccessException("Only a signed-in or presence-proved person can answer a confirmation.");
            }

            return;
        }

        if (caller.Kind != CallerKind.User
            || !string.IsNullOrEmpty(caller.ConversationId)
            || string.IsNullOrEmpty(caller.PersonId))
        {
            throw new UnauthorizedAccessException("Only a signed-in or presence-proved person can answer a confirmation.");
        }
    }
}
