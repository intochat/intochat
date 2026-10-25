using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Modules.Postgres;

namespace IntoChat.OS.Modules.Postgres.Tests.Integration;

public sealed class ContractShapeFacts
{
    [Fact]
    public void TheContractsAssemblyExportsTheServerAndTheTablesVocabularyOnly()
    {
        var assembly = typeof(IPostgresServer).Assembly;
        var neurons = assembly.GetExportedTypes()
            .Where(type => type.IsInterface && typeof(INeuron).IsAssignableFrom(type))
            .Select(type => type.FullName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["IntoChat.OS.Modules.Postgres.IPostgresServer", "IntoChat.OS.Modules.Postgres.Tables.ITable"], neurons);
        var signals = assembly.GetExportedTypes().Where(type => typeof(Signal).IsAssignableFrom(type) && type != typeof(Signal))
            .Select(type => type.Namespace).Distinct().ToArray();
        Assert.Equal(["IntoChat.OS.Modules.Postgres.Tables.Signals"], signals);
    }
}
