using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Contracts;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Windows;

internal sealed class WindowsConsentedFolderRootProvider(
    IServiceProvider services,
    MachineId thisMachine) : IConsentedFolderRootProvider
{
    public async Task<string[]> GetConsentedRootsAsync(string brainId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(brainId))
        {
            return [];
        }

        try
        {
            var edge = services.GetService<EdgeAuthority>();
            using var scope = edge?.Enter(edge.Module(WindowsModule.ManifestName, new BrainId(brainId)));
            var connectionKey = ConnectionKeys.For(brainId, WindowsModule.ManifestName, thisMachine.Value);
            var windows = SiloNeurons.Get<IWindows>(services, PlainNeuronContracts.IdOf(typeof(IWindows), connectionKey));
            return await windows.Roots().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<WindowsConsentedFolderRootProvider>().LogSwallowed(error, "reading consented Windows folder roots");
            return [];
        }
    }
}
