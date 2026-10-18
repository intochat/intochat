using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// An owner-scoped neuron exercises the real call filter's recorded-connection exception.
[Alias("tests.csharp.connection-probe")]
public interface IConnectionProbe : INeuron
{
    Task<int> Read();
}

public sealed class ConnectionProbe : Neuron, IConnectionProbe
{
    public Task<int> Read() => Task.FromResult(42);
}
