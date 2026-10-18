using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Postgres;

namespace DigitalBrain.Modules.Postgres.Tests.Integration;

public sealed class ContractShapeFacts
{
    [Fact]
    public void TheContractsAssemblyExportsTheServerAndTheTablesVocabularyOnly()
    {
        var assembly = typeof(IPostgresServer).Assembly;
        var neurons = assembly.GetExportedTypes()
            .Where(type => type.IsInterface && typeof(INeuron).IsAssignableFrom(type))
            .Select(type => type.FullName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["DigitalBrain.Postgres.IPostgresServer", "DigitalBrain.Postgres.Tables.ITable"], neurons);
        var signals = assembly.GetExportedTypes().Where(type => typeof(Signal).IsAssignableFrom(type) && type != typeof(Signal))
            .Select(type => type.Namespace).Distinct().ToArray();
        Assert.Equal(["DigitalBrain.Postgres.Tables.Signals"], signals);
    }
}
