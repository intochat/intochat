using DigitalBrain.Platform.Mesh;

namespace DigitalBrain.Platform.Trust;

internal interface INodeDirectoryClient
{
    // R75: the trust deployment's identity, minted once by the trust side; a machine keeps its memory of the
    // deployment (seen versions, journals, the signed-call counter) under it.
    Task<string> DeploymentId();

    // R41/R52/R55: binds this machine's key, and through it the machine, to one account it serves.
    Task RegisterMachine(string machineId, byte[] publicKey, string account, byte[] pairingSecretHash, byte[] selfSignature);

    // A machine as the trust side knows it (T20-2 check 5, T20-6): its key, accounts and announced candidates.
    Task<RegisteredMachine?> Machine(string machineId);

    // Where this machine may be reached directly, signed by it.
    Task Announce(MachineSigned<MachineCandidates> candidates);

    Task Register(string nodeId, string ownerAccount);

    Task SetOnline(string nodeId, bool online);

    Task ReportAudit(string nodeId, bool passed);

    Task<string[]> Place(int count, string[] exclude);

    Task<StorageNodeView[]> List();

    Task Put(string nodeId, string cid, byte[] sealedShard);

    Task<byte[]?> Get(string nodeId, string cid);

    Task<byte[]?> Prove(string nodeId, string cid, byte[] nonce);

    Task Discard(string nodeId, string cid);
}
