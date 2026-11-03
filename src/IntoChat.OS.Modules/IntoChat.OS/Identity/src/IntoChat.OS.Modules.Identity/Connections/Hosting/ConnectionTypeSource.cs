using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Hosting;

// The connectors a module declares (ConnectorDefinition.DeclaredBy) as compiled type entries.
internal sealed class ConnectionTypeSource : IModuleTypeSource
{
    public IEnumerable<BrainType> TypesOf(OsModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ConnectorDefinition.DeclaredBy(module.Type).Select(connector => Connector(connector, module.Id.ToString()));
    }

    private static BrainType Connector(ConnectorDefinition connector, string provider)
        => new(connector.Id, connector.Id, DigitalBrain.TypeKind.Connector, provider, connector.DisplayName,
            [.. connector.Neurons.Concat(connector.Signals).Select(type => type.FullName!)], []);
}
