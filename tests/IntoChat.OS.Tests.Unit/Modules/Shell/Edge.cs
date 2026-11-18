using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;

namespace IntoChat.OS.Modules.Flutter;

// What the authenticated edge stamps on a person's request: TestOS calls carry no caller of their own, and a field or view refuses
// a call with none.
internal static class Edge
{
    public static IDisposable AsPerson(string brain = "owner") => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("person", "person", new BrainId(brain)));
}
