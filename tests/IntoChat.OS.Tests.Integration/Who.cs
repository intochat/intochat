using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;

namespace IntoChat.OS.Tests;

// Who a fact stamps on a call, by the caller it means: the edge mints each one the way the product's edge does.
public enum Who
{
    Person,
    App,
    Agent,
    Scheduler,
    Kernel,
}

internal static class Stamp
{
    public static Caller Of(EdgeAuthority edge, Who who, BrainId brain, string principal = "person", string? account = null, string? app = null, string? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var person = new Actor.Person(principal, account ?? principal);
        return who switch
        {
            Who.Person => edge.Person(person.Principal, person.Account, brain, conversation),
            Who.App => edge.App(app ?? $"{brain.Value}/apps/test-app", person, brain, conversation),
            Who.Agent => edge.Agent(person, brain, conversation),
            Who.Scheduler => edge.Scheduler(brain),
            _ => edge.Kernel(brain),
        };
    }

    public static IDisposable As(this INeuronRuntime runtime, Who who, string principal = "person", string? account = null, string? app = null, string? conversation = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Edge.Enter(Of(runtime.Edge, who, runtime.Brain, principal, account, app, conversation));
    }
}
