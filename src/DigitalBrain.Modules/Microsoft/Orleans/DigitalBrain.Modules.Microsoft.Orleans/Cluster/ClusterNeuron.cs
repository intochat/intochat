using DigitalBrain.Framework;
using global::Orleans.Runtime;

namespace DigitalBrain.Microsoft.Orleans.Cluster;

[GrainType("orleans.cluster")]
internal sealed class ClusterNeuron(IClusterMembershipFeed membership) : Neuron, ICluster, IRetainsSignals, IClusterMembershipReceiver
{
    private ClusterSnapshot? _last;
    private readonly Guid activation = Guid.NewGuid();
    private IAsyncDisposable? subscription;
    private long membershipVersion = long.MinValue;

    internal async Task StartAsync()
    {
        var receiver = this.AsReference<IClusterMembershipReceiver>();
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
            await PublishAsync(new ClusterChanged(next));
            _last = next;
        }
        if (snapshot is not null) { membershipVersion = snapshot.Version; }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (subscription is not null) { await subscription.DisposeAsync(); }
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task<RetainedSignal[]> Retained(string signal, long since)
        => Task.FromResult<RetainedSignal[]>(signal == nameof(ClusterChanged) && _last is not null && _last.Version > since
            ? [new RetainedSignal(_last.Version, new ClusterChanged(_last) { Publisher = this.GetPrimaryKeyString() })] : []);
}

[Alias("orleans.cluster-membership-receiver")]
internal interface IClusterMembershipReceiver : IGrainWithStringKey
{
    Task MembershipChanged(Guid activation, ClusterSnapshot? snapshot);
}

internal sealed class ClusterActivation : IActivationHandler<ClusterNeuron>
{
    public Task Activated(ClusterNeuron neuron, CancellationToken cancellationToken) => neuron.StartAsync();
}

internal static class ThisAssembly
{
    public static readonly string Sha = typeof(ThisAssembly).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+').LastOrDefault() ?? "source";
}
