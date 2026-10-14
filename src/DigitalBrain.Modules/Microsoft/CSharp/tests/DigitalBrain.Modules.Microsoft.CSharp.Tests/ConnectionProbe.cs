using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

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
