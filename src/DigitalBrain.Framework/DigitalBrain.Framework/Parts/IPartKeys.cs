namespace DigitalBrain.Framework.Parts;

// The key that encrypts one part (Q6). Only a silo holding the part's lease asks for it; no grain call carries it.
public interface IPartKeys
{
    Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default);

    // Revoking a circle rotates one part key; the part log re-seals from its next snapshot.
    Task<byte[]> Rotate(PartId part, string ownerAccount, CancellationToken cancellationToken = default);
}
