namespace DigitalBrain.Microsoft.Aspire.Deployment;

// Roll hands each step to this one-way self-reference: the caller's request ends at once, and every step persists
// before and after its side effect, so an activation that dies mid-roll resumes at the step it was on.
internal interface IDeploymentDriver : IGrainWithStringKey
{
    [global::Orleans.Concurrency.OneWay] Task Step();
}
