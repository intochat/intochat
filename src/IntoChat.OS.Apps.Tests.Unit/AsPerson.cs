using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;

namespace IntoChat.OS.Apps.Tests.Unit;

// What the authenticated edge stamps on a person's request: TestOS calls carry no caller of their own, and an app's records refuse
// a call with none.
internal static class Edge
{
    public static IDisposable AsPerson(string brain = "local") => RuntimeCallerContext.Stamp(new CapabilityCaller("person", "person", brain));
}
