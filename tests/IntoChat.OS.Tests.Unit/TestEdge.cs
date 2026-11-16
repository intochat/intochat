using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Testing;

namespace IntoChat.OS.Tests;

internal static class TestEdge
{
    private static readonly TestOS Host = TestOS.Local();

    public static EdgeAuthority Authority => Host.Edge;

    public static Caller From(CapabilityCaller caller) => Host.Edge.From(caller);
}
