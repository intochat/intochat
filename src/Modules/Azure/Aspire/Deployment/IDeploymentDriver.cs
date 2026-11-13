namespace IntoChat.OS.Modules.Microsoft.Aspire.Deployment;

// Membership changes enter the deployment neuron through this self-reference, on its scheduler.
// A plain contract of the deployment neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("aspire.deployment-driver"), DigitalBrain.Contracts.DefaultNeuronType("aspire.deployment")]
internal interface IDeploymentDriver
{
    [DigitalBrain.Contracts.Interleaves]
    Task MembershipChanged(Guid activation, IntoChat.OS.Modules.Microsoft.Orleans.ClusterSnapshot? snapshot);
}
