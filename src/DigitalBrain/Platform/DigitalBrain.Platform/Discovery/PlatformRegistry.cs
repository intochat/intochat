using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Platform.Discovery;

internal sealed class PlatformRegistry(IEnumerable<IRegistryInventoryProvider> providers) : IRegistry
{
    public Task<RegistryInventory> Persisted(string? typeId = null, CancellationToken ct = default)
        => Discover(false, typeId, ct);
    public Task<RegistryInventory> Active(string? typeId = null, CancellationToken ct = default)
        => Discover(true, typeId, ct);

    private async Task<RegistryInventory> Discover(bool active, string? typeId, CancellationToken ct)
    {
        var caller = CallerContextStamper.Require();
        if (!CallerContextStamper.IsTrusted(caller)) { throw new UntrustedCallerException("The inventory requires a trusted caller."); }
        ct.ThrowIfCancellationRequested();
        var sources = providers.ToArray();
        var answers = await Task.WhenAll(sources.Select(async provider =>
        {
            try
            {
                var records = await (active ? provider.Active(typeId, ct) : provider.Persisted(typeId, ct)).WaitAsync(ct);
                return new RegistryInventory(records.Select(record => record with { Domain = provider.Domain }).ToArray(), [], []);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new RegistryInventory([], [], [new(provider.Domain, "Inventory is temporarily unavailable for this domain.")]); }
        }));
        return new(answers.SelectMany(answer => answer.Records).ToArray(), sources.Select(provider => provider.Domain).ToArray(),
            answers.SelectMany(answer => answer.Errors).ToArray());
    }
}
