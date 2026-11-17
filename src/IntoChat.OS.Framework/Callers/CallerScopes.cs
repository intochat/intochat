using DigitalBrain;
using DigitalBrain.Access;

namespace IntoChat.OS.Framework;

// Acting in a brain named by a route or an install: the current stamped caller, re-scoped to that brain. Only a person calling
// for themselves is re-scoped; anyone else keeps their own stamp, so an app or an agent can never become a person here.
public static class CallerScopes
{
    public static async Task<T> InBrain<T>(this EdgeAuthority edge, BrainId brain, Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(call);
        using (edge.Current is { Actor: Actor.Person person, Direct: true } current
            ? edge.Enter(edge.Person(person.Principal, person.Account, brain, current.Conversation))
            : null)
        {
            return await call().ConfigureAwait(false);
        }
    }

    public static Task InBrain(this EdgeAuthority edge, BrainId brain, Func<Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return edge.InBrain(brain, async () => { await call().ConfigureAwait(false); return true; });
    }
}
