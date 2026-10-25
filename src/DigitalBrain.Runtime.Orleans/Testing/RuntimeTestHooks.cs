using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Testing;

/// <summary>Runtime controls for facts (04-12, 04-15). Not a production entry.</summary>
internal static class RuntimeTestHooks
{
    /// <summary>Runs a checkpoint of <paramref name="brain"/>'s journal now, trimming each publisher at its retention floor.</summary>
    public static async Task ForceCheckpoint(IServiceProvider services, string brain, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var log = await services.GetRequiredService<BrainLogs>().For(brain).ConfigureAwait(false);
        await log.Checkpoint(ct).ConfigureAwait(false);
    }
}
