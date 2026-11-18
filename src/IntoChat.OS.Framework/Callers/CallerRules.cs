using System.Diagnostics.CodeAnalysis;
using DigitalBrain.Access;

namespace IntoChat.OS.Framework;

// The authority checks the platform contracts make, read from the caller the kernel stamped, never from an argument.
internal static class CallerRules
{
    public static bool IsKernel(Caller? caller) => caller?.Actor is Actor.Kernel;

    // A person calling for themselves: not relayed through a neuron, not an agent or app acting for them.
    public static bool IsDirectPerson(Caller? caller, string principal) =>
        caller is { Actor: Actor.Person person, Direct: true } && string.Equals(person.Principal, principal, StringComparison.Ordinal);

    // Whom the caller acts for: the person themselves, or the person an app or agent acts for; null for the kernel, a module or the scheduler.
    public static string? ActsFor(Caller? caller) => caller?.Actor switch
    {
        Actor.Person person => person.Principal,
        Actor.App app => app.For.Principal,
        Actor.Agent agent => agent.For.Principal,
        _ => null,
    };

    public static bool IsApp(Caller? caller, [NotNullWhen(true)] out string? key)
    {
        key = caller?.Actor is Actor.App app ? app.Key : null;
        return key is not null;
    }
}
