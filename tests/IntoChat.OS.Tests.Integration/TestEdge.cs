using DigitalBrain.Access;
using DigitalBrain.Testing;

namespace IntoChat.OS.Tests;

internal static class TestEdge
{
    private static readonly TestOS Host = TestOS.Local();

    public static EdgeAuthority Authority => Host.Edge;

    public static ICallerAmbient Callers => Host.Callers;
}
