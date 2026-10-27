using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Cluster;

[DigitalBrain.Neuron("orleans.cluster")]
internal sealed class ClusterNeuron(IClusterMembershipFeed membership) : DigitalBrain.Neuron, ICluster, IClusterMembershipReceiver
{
    private ClusterSnapshot? _last;
    private readonly Guid activation = Guid.NewGuid();
    private IAsyncDisposable? subscription;
    private long membershipVersion = long.MinValue;

    // Membership changes come in from the feed off any turn; each is handed back to this neuron as a call, so it is a turn.
    // The runtime retains the last ClusterChanged for a watch opened later (the hand-written retention went with the grain).
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        var receiver = Get<IClusterMembershipReceiver>(Id);
        subscription = membership.Subscribe(snapshot => receiver.MembershipChanged(activation, snapshot));
        await MembershipChanged(activation, membership.Current);
    }

    public Task<ClusterSnapshot> Read() => Task.FromResult(_last ?? membership.Current);

    public async Task MembershipChanged(Guid expectedActivation, ClusterSnapshot? snapshot)
    {
        if (expectedActivation != activation) { return; }
        if (snapshot is not null && snapshot.Version < membershipVersion) { return; }
        var next = new ClusterSnapshot(snapshot?.Silos ?? [], (_last?.Version ?? 0) + 1);
        if (_last is null || !_last.Silos.SequenceEqual(next.Silos))
        {
            await Publish(new ClusterChanged(next));
            _last = next;
        }
        if (snapshot is not null) { membershipVersion = snapshot.Version; }
    }

    protected override async Task OnDeactivate(DigitalBrain.DeactivationCause cause, CancellationToken cancellationToken)
    {
        if (subscription is not null) { await subscription.DisposeAsync(); }
    }
}

// The feed's way back into the neuron: one call per membership change, refused for a stale activation.
[DigitalBrain.Contracts.NeuronAlias("orleans.cluster-membership-receiver"), DigitalBrain.Contracts.DefaultNeuronType("orleans.cluster")]
public interface IClusterMembershipReceiver
{
    Task MembershipChanged(Guid activation, ClusterSnapshot? snapshot);
}

internal static class ThisAssembly
{
    public static readonly string Sha = typeof(ThisAssembly).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+').LastOrDefault() ?? "source";
}
