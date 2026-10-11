namespace DigitalBrain.Framework.Parts;

// The key that encrypts one part (Q6). Only a silo holding the part's lease asks for it; no grain call carries it.
public interface IPartKeys
{
    // An owner's part key never rotates; a circle brain's part keys rotate on removal (ICirclePartKeys), which re-seals
    // the head, a snapshot and the journal under the new key.
    Task<byte[]> Key(PartId part, string ownerAccount, CancellationToken cancellationToken = default);
}
