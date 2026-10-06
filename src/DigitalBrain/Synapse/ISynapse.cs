namespace DigitalBrain;

// A durable connection with a program: it watches neurons and acts through neuron contracts,
// lives in its brain and survives every process. Dialects (a C# file, later others) derive from it.
[Alias("synapse")]
public interface ISynapse : INeuron
{
    Task<SynapseSnapshot> Read(CancellationToken cancellationToken = default);
    Task Write(string program, CancellationToken cancellationToken = default);
    // Starting may build a sandbox on any runtime; the timeout is the word's, not a dialect's.
    [ResponseTimeout("00:05:00")] Task<SynapseSnapshot> Start(CancellationToken cancellationToken = default);
    [ResponseTimeout("00:05:00")] Task<SynapseSnapshot> Stop(CancellationToken cancellationToken = default);
    [ResponseTimeout("00:05:00")] Task Delete(CancellationToken cancellationToken = default);
}
