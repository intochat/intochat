using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Flutter;

// Edge presence helper for OS shell UI routes: a person-gated action (such as answering a confirmation) needs the person
// calling directly, outside any conversation; an app, an agent (MCP) or no caller at all is refused.
internal static class ShellPresenceGate
{
    public static void RequireDirectPerson(Caller? caller)
    {
        if (caller is not { Direct: true, Conversation: null, Actor: Actor.Person { Principal.Length: > 0 } })
        {
            throw new UnauthorizedAccessException("Only a signed-in or presence-proved person can answer a confirmation.");
        }
    }
}
