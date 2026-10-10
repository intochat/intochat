namespace DigitalBrain.Framework.Parts;

// The key that encrypts one part (Q6). Only a silo holding the part's lease asks for it; no grain call carries it.
public interface IPartKeys
{
    // No rotation yet: replacing a part key without re-sealing its head, segments, snapshot and journal would make
    // every byte of the part unreadable. It returns with the re-seal (circle revocation).
    Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default);
}
