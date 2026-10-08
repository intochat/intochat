using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Microsoft.CSharp.Catalog;

internal sealed class ContractVocabulary(ContractCatalog catalog) : IContractVocabulary
{
    private readonly ContractVocabularyEntry[] entries = catalog.VisibleTypes
        .Where(t => ContractCatalog.IsNeuron(t) || t != typeof(Signal) && typeof(Signal).IsAssignableFrom(t))
        .Select(t => new ContractVocabularyEntry(t.FullName!, t.Name, ContractCatalog.IsNeuron(t) ? "neuron" : "signal", t.Assembly.GetName().Name ?? "", null))
        .OrderBy(e => e.QualifiedName, StringComparer.Ordinal).ToArray();

    public ContractVocabularyEntry[] Read() => [.. entries];
}
