using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Mesh;

// Registers the platform's seed nodes under stable ids, so every boot re-registers the same nodes.
internal sealed class SeedNodeRegistration(IOptions<MeshOptions> options, IGrainFactory grains) : IStartupTask
{
    public const string Owner = "platform";

    public async Task Execute(CancellationToken cancellationToken)
    {
        var directory = grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);
        for (var n = 0; n < options.Value.SeedNodes; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await directory.Register($"seed-{n}", Owner);
        }
    }
}
