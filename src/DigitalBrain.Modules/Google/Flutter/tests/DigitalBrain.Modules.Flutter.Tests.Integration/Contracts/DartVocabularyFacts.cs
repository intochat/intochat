using System.Reflection;
using System.Text.RegularExpressions;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flutter;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Contracts;

// W2 PR 0 (ui-contract 7.1): until the generator replaces them (PR 3), the shell's hand-written mirrors of the C#
// vocabulary are checked against it here: RendererRegistry.uiKinds ("Mirrors UIVocabulary") and the NodeContracts
// table the shell watches by contract full name and change-signal short name.
public sealed partial class DartVocabularyFacts
{
    // Kinds the C# vocabulary declares that the renderer registry does not list: the shell draws chat and composer
    // through the workspace, not NeuronView. Shrinking this list is fine; growing it is a design decision.
    private static readonly string[] CSharpOnlyKinds = ["chat", "composer"];

    internal static string[] Vocabulary() => [.. typeof(UIVocabulary).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith("Type", StringComparison.Ordinal))
        .Select(field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal)];

    internal static string[] RendererKinds()
    {
        var list = UiKinds().Match(UiContractFiles.Dart("ui", Path.Combine("composition", "renderer_registry.dart")));
        Assert.True(list.Success, "RendererRegistry.uiKinds was not found.");
        var body = list.Groups["body"].Value;
        var kinds = Quoted().Matches(body).Select(match => match.Groups["value"].Value).ToArray();
        // Every comma-separated entry must be a plain quoted kind; anything else is a shape this reader does not know.
        var entries = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        Assert.True(kinds.Length == entries, $"RendererRegistry.uiKinds has {entries} entries but {kinds.Length} parsed as quoted kinds.");
        return [.. kinds.Order(StringComparer.Ordinal)];
    }

    internal static (string Kind, string Contract, string Changed)[] NodeContracts()
    {
        var source = UiContractFiles.Dart("core", "node_contracts.dart");
        var table = NodeContract().Matches(source)
            .Select(match => (match.Groups["kind"].Value, match.Groups["contract"].Value, match.Groups["changed"].Value)).ToArray();
        // Every NodeContract(...) construction in the table must parse; the class declaration's constructor does not count.
        var entries = Regex.Count(source, @"NodeContract\(") - Regex.Count(source, @"const NodeContract\(");
        Assert.True(table.Length == entries, $"node_contracts.dart has {entries} NodeContract entries but {table.Length} parsed.");
        return table;
    }

    [Fact]
    public void TheRendererRegistryMirrorsTheVocabulary()
    {
        Assert.Equal(Vocabulary().Except(CSharpOnlyKinds), RendererKinds());
    }

    [Fact]
    public void EveryNodeContractNamesAVocabularyKindItsContractAndItsChangeSignal()
    {
        var table = NodeContracts();
        Assert.Equal(21, table.Length);
        var assemblies = new[] { typeof(UIVocabulary).Assembly, typeof(IScene).Assembly };
        var offenders = new List<string>();
        foreach (var (kind, contract, changed) in table)
        {
            if (!Vocabulary().Contains(kind)) { offenders.Add($"{kind}: not a UIVocabulary kind"); }
            var type = assemblies.Select(assembly => assembly.GetType(contract)).SingleOrDefault(found => found is not null);
            if (type is null || !type.IsInterface || !typeof(INeuron).IsAssignableFrom(type)) { offenders.Add($"{kind}: {contract} is not a neuron contract"); continue; }
            if (!type.Assembly.GetExportedTypes().Any(signal => typeof(Signal).IsAssignableFrom(signal) && signal.Name == changed && signal.Namespace!.StartsWith(type.Namespace!, StringComparison.Ordinal)))
            { offenders.Add($"{kind}: no signal {changed} beside {contract}"); }
        }
        Assert.True(offenders.Count == 0, "node_contracts.dart drifted from the C# contracts:\n" + string.Join('\n', offenders));
    }

    [GeneratedRegex(@"static const List<String> uiKinds = \[(?<body>[^\]]*)\];")]
    private static partial Regex UiKinds();

    [GeneratedRegex(@"'(?<value>[^']*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"'(?<kind>\w+)':\s*NodeContract\(\s*'(?<contract>[\w.]+)',\s*'(?<changed>\w+)',?\s*\)")]
    private static partial Regex NodeContract();
}
