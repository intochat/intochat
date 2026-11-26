using DigitalBrain.Access;

namespace DigitalBrain.Testing.Tests.Unit;

// Stamps a caller on the TestOS ambient, which every TestOS shares. A fact that drives the kernel's own machinery (installs,
// deliveries, turns, state views) runs as the kernel; a fact about a person or an app stamps that caller instead.
internal static class TestStamp
{
    private static readonly EdgeAuthority Edge = new(TestCallerAmbient.Instance);

    public static IDisposable Kernel() => Edge.EnterKernel(BrainId.Kernel);

    public static IDisposable Person(string brain, string principal = "ada")
        => Edge.Enter(Edge.Person(principal, principal + "-account", new BrainId(brain)));

    public static IDisposable App(string brain, string key, string principal = "ada")
        => Edge.Enter(Edge.App(key, new Actor.Person(principal, principal + "-account"), new BrainId(brain)));
}
