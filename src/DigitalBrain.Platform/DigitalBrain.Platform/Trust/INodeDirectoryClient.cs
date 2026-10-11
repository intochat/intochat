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

    // T23-5: this machine's own storage node, signed by it for an account it serves.
    Task RegisterNode(MachineSigned<NodeRegistration> registration);

    Task Heartbeat(string nodeId, long usedBytes);

    // T23-10: what this machine's audit of a node found; the trust side records it as returned (ShardBytes clamped).
    Task<AuditReport> ReportAudit(AuditReport report);

    // T23-6: never onto a node of this machine's own accounts; only nodes with room for shardBytes. R142: the placement
    // id names the result in audit reports.
    Task<Placement> Place(int count, string[] exclude, int shardBytes);

    Task<StorageNodeView[]> List();

    // T23-11/R55: the storage credits an account this machine serves has earned and not yet pulled into its ledger.
    Task<StorageCredit[]> PendingCredits(string account);

    Task AcknowledgeCredits(string account, string[] entryIds);

    Task Put(string nodeId, string cid, byte[] sealedShard);

    Task<byte[]?> Get(string nodeId, string cid);

    Task<byte[]?> Prove(string nodeId, string cid, byte[] nonce);

    Task Discard(string nodeId, string cid);
}
