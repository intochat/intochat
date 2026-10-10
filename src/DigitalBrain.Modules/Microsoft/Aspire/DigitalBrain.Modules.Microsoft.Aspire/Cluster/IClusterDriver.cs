namespace DigitalBrain.Microsoft.Aspire.Cluster;

// Roll hands each step to this one-way self-reference: the caller's request ends at once, and every step persists
// before and after its side effect, so an activation that dies mid-roll resumes at the step it was on.
internal interface IClusterDriver : IGrainWithStringKey
{
    [Orleans.Concurrency.OneWay] Task Step();
}
