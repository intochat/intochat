using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;

namespace IntoChat.OS.Connections.Hosting;

// The connectors a module declares (ConnectorDefinition.DeclaredBy) as compiled type entries.
internal sealed class ConnectionTypeSource : IModuleTypeSource
{
    public IEnumerable<TypeEntry> TypesOf(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ConnectorDefinition.DeclaredBy(module.GetType()).Select(connector => Connector(connector, module.Id.ToString()));
    }

    private static TypeEntry Connector(ConnectorDefinition connector, string provider)
        => new(connector.Id, connector.Id, TypeKind.Connector, provider, connector.DisplayName,
            [.. connector.Neurons.Concat(connector.Signals).Select(type => type.FullName!)], []);
}
