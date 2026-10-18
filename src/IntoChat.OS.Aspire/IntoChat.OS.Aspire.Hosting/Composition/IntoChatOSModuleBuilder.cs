using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting;

public sealed class IntoChatOSModuleBuilder<TModule>
    where TModule : IIntoChatOSModuleHosting, new()
{
    private static readonly string ModuleId = new TModule().Module.ToString();

    public IntoChatOSModuleBuilder(IntoChatOSBuilder intoChatOSBuilder) => IntoChatOSBuilder = intoChatOSBuilder;

    public IntoChatOSBuilder IntoChatOSBuilder { get; }

    public IResourceBuilder<IntoChatOSModuleResource> Resource
        => IntoChatOSBuilder.GetOrAddModuleNode(ModuleId);

    public void AddProjection(IntoChatOSModuleProjection projection)
        => IntoChatOSBuilder.AddProjection(projection);

    // The standard module-hosting shape: one state per brain, projected to every silo host.
    public TState GetOrAddProjection<TState>(Func<IntoChatOSBuilder, TState> create)
        where TState : IntoChatOSModuleProjection
    {
        var state = IntoChatOSBuilder.GetOrAddState(create, out var added);
        if (added) { AddProjection(state); }
        return state;
    }
}
