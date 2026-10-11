using DigitalBrain;
using Orleans.Concurrency;

namespace DigitalBrain.Equifax;

// The credit bureau, keyed {brain}/connection/equifax/{id}: a "credentials" connection reads with the
// client a user pasted, a "deployment" connection with the deployment's Equifax integration. Every read publishes BureauReportRead or BureauReportFailed.
[Alias("equifax")]
[Orleans.Metadata.DefaultGrainType("equifax")]
public interface IEquifax : INeuron
{
    Task<BureauReport> ReadCreditReport(ReadCreditReport query);

    [ReadOnly]
    Task<BureauReport[]> ReadRecent(int limit);
}
