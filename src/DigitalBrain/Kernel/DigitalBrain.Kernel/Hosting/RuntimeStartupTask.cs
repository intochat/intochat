using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Signals;

namespace DigitalBrain.Kernel;

internal sealed class RuntimeStartupTask(ModuleInventory modules, LocalSignalHub signals) : IStartupTask
{
    public Task Execute(CancellationToken cancellationToken)
    {
        foreach (var module in modules.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            signals.Publish(new ModuleLoaded(module.AssemblyQualifiedName!));
        }
        return Task.CompletedTask;
    }
}
