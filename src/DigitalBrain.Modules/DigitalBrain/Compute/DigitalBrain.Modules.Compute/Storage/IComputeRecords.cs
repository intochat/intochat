using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Compute.Storage;

// Storage behind the account-scoped usage and ledger neurons, keyed "meters/{period}" (platform-wide)
// and "{brain}/usage/{hash(account, brain)}": those neurons answer the scope question, this one is
// reached only from inside the cluster. The owner rule would read "meters" as a brain id.
[Internal]
internal interface IComputeRecords : INeuron
{
    Task<int> Put(ComputeStoredRecord[] records, bool overwrite);
    Task<ComputeStoredRecord[]> Read();
    Task<ComputeRecordPage> Page(string? before, int limit);
}
