using DigitalBrain.Apps;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Flutter.Space;

[GrainType("ui.space")]
internal sealed class SpaceNeuron(IGrainFactory grains, IKernel kernel, CompiledTypes compiled, ILogger<SpaceNeuron> logger) : Neuron, ISpace
{
    public async Task Rebuild()
    {
        var brain = this.GetPrimaryKeyString();
        var types = compiled.Entries;
        if (!types.Any(type => type.Name == typeof(IApp).FullName) || !types.Any(type => type.Name == typeof(IScene).FullName)) { return; }
        // This is the module's derived write, even when an app's Show caused the invalidation.
        var inherited = RequestContext.Get(CallerContextStamper.RequestContextKey);
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            var workspace = await grains.GetGrain<IWorkspace>(brain).Read();
            List<SpaceComposer.SpaceEntry> entries = [];
            await foreach (var instance in kernel.InstancesIn(brain, kernel.TypeOf(typeof(IApp))))
            {
                try
                {
                    var app = await grains.GetGrain<IApp>(instance.Key).Read();
                    entries.Add(new(instance.Key, app.Manifest.Title, app.Manifest.Description, app.Presentation, app.Manifest.Toolbar ?? []));
                }
                catch (Exception error) { logger.LogWarning(error, "Could not read {App} while rebuilding space {Brain}.", instance.Key, brain); }
            }
            var scene = grains.GetGrain<IScene>(brain + "/space");
            var current = await scene.Read();
            await scene.Set(SpaceComposer.Compose(workspace, [.. entries]), current.Revision);
        }
        finally
        {
            if (inherited is not null) { RequestContext.Set(CallerContextStamper.RequestContextKey, inherited); }
        }
    }
}

internal sealed class SpaceActivation : IActivationHandler<SpaceNeuron>
{
    public Task Activated(SpaceNeuron neuron, CancellationToken cancellationToken) => neuron.Rebuild();
}
