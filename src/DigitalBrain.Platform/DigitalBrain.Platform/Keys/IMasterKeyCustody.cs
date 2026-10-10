namespace DigitalBrain.Platform.Keys;

// Holds each user's master key (Q3, Q6). Callers hand it secrets to wrap; the master key never leaves it.
// Stage A derives it from a configured root; Task 19 moves custody into the machine's TPM.
internal interface IMasterKeyCustody
{
    byte[] Wrap(string accountId, string context, byte[] secret);

    byte[] Unwrap(string accountId, string context, byte[] wrapped);
}
