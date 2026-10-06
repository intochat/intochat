using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Framework;

[GrainType("brain")]
internal sealed class BrainNeuron : Neuron<BrainState>, IBrain
{
    private readonly IPersistentState<BrainState> _store;
    private readonly IBrainProvisioner[] _provisioners;
    private readonly ILogger<BrainNeuron> _logger;

    public BrainNeuron(
        [PersistentState("brain", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<BrainState> store,
        IEnumerable<IBrainProvisioner> provisioners, ILogger<BrainNeuron> logger)
        : base(store)
    {
        _store = store;
        _provisioners = [.. provisioners];
        _logger = logger;
    }

    public Task<BrainSnapshot> Read() => Task.FromResult(ToSnapshot());

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        if (Snapshot.EstablishedAt != default)
        {
            await PublishAsync(new Activated(this.GetPrimaryKeyString(), Snapshot.OwnerAccountId, Snapshot.Name));
        }
    }

    public async Task<BrainSnapshot> Establish(EstablishBrain request)
    {
        if (Snapshot.EstablishedAt == default)
        {
            Snapshot.Name = request.Name;
            Snapshot.OwnerAccountId = request.OwnerAccountId;
            Snapshot.EstablishedAt = DateTimeOffset.UtcNow;
            await _store.WriteStateAsync();
            var activated = new Activated(this.GetPrimaryKeyString(), Snapshot.OwnerAccountId, Snapshot.Name);
            await PublishAsync(activated);
            await Provision(activated);
        }
        return ToSnapshot();
    }

    private async Task Provision(Activated activated)
    {
        foreach (var provisioner in _provisioners)
        {
            try { await provisioner.Provision(activated, CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            { _logger.LogWarning(error, "Provisioning brain {Brain} with {Provisioner} failed.", activated.BrainId, provisioner.GetType().Name); }
        }
    }

    private BrainSnapshot ToSnapshot() => new()
    {
        BrainId = this.GetPrimaryKeyString(),
        Name = Snapshot.Name,
        OwnerAccountId = Snapshot.OwnerAccountId,
        EstablishedAt = Snapshot.EstablishedAt,
    };
}
