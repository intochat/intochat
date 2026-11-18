using DigitalBrain.Access;
using DigitalBrain.Testing;

namespace IntoChat.OS.Tests;

internal static class TestEdge
{
    private static readonly TestOS Host = TestOS.Local();

    public static EdgeAuthority Authority => Host.Edge;

    public static ICallerAmbient Callers => Host.Callers;

    // A host's services as the edge grants authority sees them: the TestOS services plus the edge authority it reads as.
    public static IServiceProvider WithEdge(IServiceProvider inner) => new EdgeServices(inner);

    private sealed class EdgeServices(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(EdgeAuthority) ? Authority : inner.GetService(serviceType);
    }
}
