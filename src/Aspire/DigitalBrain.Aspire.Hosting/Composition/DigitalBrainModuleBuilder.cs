using Aspire.Hosting.ApplicationModel;

namespace DigitalBrain.Aspire.Hosting;

public sealed class DigitalBrainModuleBuilder<TModule>
    where TModule : IDigitalBrainModuleHosting, new()
{
    private static readonly string ModuleId = new TModule().Id;

    public DigitalBrainModuleBuilder(DigitalBrainBuilder digitalBrainBuilder) => DigitalBrainBuilder = digitalBrainBuilder;

    public DigitalBrainBuilder DigitalBrainBuilder { get; }

    public IResourceBuilder<DigitalBrainModuleResource> Resource
        => DigitalBrainBuilder.GetOrAddModuleNode(ModuleId);

    public void AddProjection(DigitalBrainModuleProjection projection)
        => DigitalBrainBuilder.AddProjection(projection);

    // The standard module-hosting shape: one state per brain, projected to every silo host.
    public TState GetOrAddProjection<TState>(Func<DigitalBrainBuilder, TState> create)
        where TState : DigitalBrainModuleProjection
    {
        var state = DigitalBrainBuilder.GetOrAddState(create, out var added);
        if (added) { AddProjection(state); }
        return state;
    }
}
