namespace DigitalBrain.Framework.Links;

// A machine's links to the other machines of its trust deployment (T20-1): one QUIC link per pair, mutually
// authenticated by the two machines' signing keys, direct when a hole punch works and relayed otherwise.
public interface IMachineTransport
{
    Task<IMachineLink> Connect(string machineId, CancellationToken cancellationToken);

    string LocalMachineId { get; }
}
