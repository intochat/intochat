using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace DigitalBrain.Testing.Module;

// The module tier's grain access over the one host; deleted in Task 20 after the sweep.
public static class BrainSimulationExtensions
{
    extension(ITestBrain brain)
    {
        public IGrainFactory Grains => brain.Services.GetRequiredService<IGrainFactory>();
    }

    public static IClusterClient Cluster(this ITestBrain brain) => Requires(brain).Services.GetRequiredService<IClusterClient>();

    public static IDigitalBrains Client(this ITestBrain brain) => Requires(brain).Services.GetRequiredService<IDigitalBrains>();

    public static Task RestartSiloAsync(this ITestBrain brain, CancellationToken cancellationToken = default)
        => Requires(brain).RestartAsync(cancellationToken);

    private static ITestBrain Requires(ITestBrain brain) => brain ?? throw new ArgumentNullException(nameof(brain));
}
