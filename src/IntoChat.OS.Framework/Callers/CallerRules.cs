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

    public static bool IsApp(Caller? caller, [NotNullWhen(true)] out string? key)
    {
        key = caller?.Actor is Actor.App app ? app.Key : null;
        return key is not null;
    }
}
