using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Access;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit;

// Stamps a caller on the Orleans ambient the way the kernel and its edge do (this assembly cannot construct a Caller itself).
internal static class Stamps
{
    public static Caller Person(string principal, string account, string brain, string? conversation = null)
        => Read(new CallerWire { Actor = CallerWire.ActorPerson, Principal = principal, Account = account, Brain = brain, Conversation = conversation });

    public static Caller App(string key, string principal, string account, string brain, string? conversation = null)
        => Read(new CallerWire { Actor = CallerWire.ActorApp, Name = key, Principal = principal, Account = account, Brain = brain, Conversation = conversation });

    public static Caller Of(byte actor, string principal, string account, string brain, string? name = null)
        => Read(new CallerWire { Actor = actor, Name = name, Principal = principal, Account = account, Brain = brain });

    public static Caller Kernel(string brain) => Read(new CallerWire { Actor = CallerWire.ActorKernel, Brain = brain, Inside = true });

    public static IDisposable Enter(Caller caller) => OrleansCallerAmbient.Instance.Enter(caller);

    public static Caller? Current => OrleansCallerAmbient.Instance.Current;

    private static Caller Read(CallerWire wire) => OrleansCallerAmbient.Read(wire)!;
}