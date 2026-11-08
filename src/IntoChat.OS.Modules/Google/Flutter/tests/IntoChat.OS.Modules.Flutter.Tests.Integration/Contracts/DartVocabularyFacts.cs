using DigitalBrain.Contracts;
using System.Reflection;
using System.Text.RegularExpressions;
using IntoChat.OS.Modules.Flame.Scene;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Contracts;

// The shell's hand-written NodeContracts table (core/lib/src/node_contracts.dart) is checked against the C# vocabulary: the
// shell watches each kept kind by contract full name and change-signal short name. The node models themselves are generated
// (DartNodeModelsFacts in IntoChat.OS.Architecture.Tests.Unit), so there is no hand-written renderer kind list to mirror.
public sealed partial class DartVocabularyFacts
{
    // The kept kinds the client reads by GET /apps/node and watches: one view tree, its fields and the hosted surfaces.
    private static readonly string[] KeptKinds =
        [UIVocabulary.ViewType, "field", UIVocabulary.ConfirmationType, UIVocabulary.ViewportType, UIVocabulary.ChatType,
            UIVocabulary.ComposerType, UIVocabulary.WebBrowserType];

    // The wire spells the field kind "field" (GET /apps/node?kind=field); its vocabulary type is the neuron alias.
    private static string VocabularyKind(string kind) => kind == "field" ? UIVocabulary.FieldType : kind;

    internal static string[] Vocabulary() => [.. typeof(UIVocabulary).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith("Type", StringComparison.Ordinal))
        .Select(field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal)];

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
    public void EveryNodeContractNamesAVocabularyKindItsContractAndItsChangeSignal()
    {
        var table = NodeContracts();
        Assert.Equal(KeptKinds.Order(StringComparer.Ordinal), table.Select(entry => entry.Kind).Order(StringComparer.Ordinal));
        var assemblies = new[] { typeof(UIVocabulary).Assembly, typeof(IScene).Assembly };
        var offenders = new List<string>();
        foreach (var (kind, contract, changed) in table)
        {
            if (!Vocabulary().Contains(VocabularyKind(kind))) { offenders.Add($"{kind}: not a UIVocabulary kind"); }
            if (kind == UIVocabulary.ConfirmationType)
            {
                // The runtime's framework signal is intentionally not declared beside its OS inbox adapter.
                Assert.Equal(typeof(DigitalBrain.Confirmation.IConfirmationInbox).FullName, contract);
                Assert.Equal(nameof(DigitalBrain.Confirmation.ConfirmationRequested), changed);
                continue;
            }
            var type = assemblies.Select(assembly => assembly.GetType(contract)).SingleOrDefault(found => found is not null);
            if (type is null || !type.IsInterface || !DigitalBrain.Contracts.PlainNeuronContracts.Is(type)) { offenders.Add($"{kind}: {contract} is not a neuron contract"); continue; }
            if (!type.Assembly.GetExportedTypes().Any(signal => typeof(DigitalBrain.Signal).IsAssignableFrom(signal) && signal.Name == changed && signal.Namespace!.StartsWith(type.Namespace!, StringComparison.Ordinal)))
            { offenders.Add($"{kind}: no signal {changed} beside {contract}"); }
        }
        Assert.True(offenders.Count == 0, "node_contracts.dart drifted from the C# contracts:\n" + string.Join('\n', offenders));
    }

    // Every kind GET /apps/node serves: the shell watches each by contract, so a missing entry throws in the browser.
    [Fact]
    public void EveryServedKindHasANodeContract()
        => Assert.Empty(KeptKinds.Except(NodeContracts().Select(entry => entry.Kind)));

    // Renderer vocabulary kinds the shell has no node contract for yet (no Flutter contract is watched for them). Known gap,
    // pinned so the guard passes at head: a kind that gains a Dart entry must leave this list (checked below); a kind
    // emitted by a UiChildRef call site must never be added here.
    private static readonly string[] VocabularyKindsWithoutADartContract =
        [UIVocabulary.TextType, UIVocabulary.ButtonType, UIVocabulary.LayoutType, UIVocabulary.CollectionType, UIVocabulary.InfoBarType,
            UIVocabulary.CardType, UIVocabulary.ImageType, UIVocabulary.ChartType, UIVocabulary.TableType, UIVocabulary.PersonType,
            UIVocabulary.FormType];

    // Runtime-compiled source apps that still compose per-widget neurons the client no longer draws (units all the way):
    // each leaves this list when it publishes a DigitalBrain.UI view instead.
    private static readonly string[] PendingWidgetEmitters = ["features/os/Circle/PublicationUi.cs", "features/os/Usage/Usage.cs"];

    [Fact]
    public void EveryKindTheServerCanEmitHasADartNodeContract()
    {
        // Emittable = renderer vocabulary + the literal kind of every UiChildRef("<kind>", ...) in production source.
        var emitters = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in Vocabulary()) { emitters[kind] = ["UIVocabulary"]; }
        foreach (var directory in UiContractFiles.ProductionSourceDirectories())
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(UiContractFiles.Root, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "bin" or "obj" or "tests" or "test") || relative.Contains(".Tests", StringComparison.Ordinal)) { continue; }
                if (PendingWidgetEmitters.Contains(relative))
                {
                    pending.Add(relative);
                    continue;
                }
                foreach (Match call in LiteralUiChildRef().Matches(File.ReadAllText(file)))
                {
                    var kind = call.Groups["kind"].Value;
                    if (!emitters.TryGetValue(kind, out var files)) { emitters[kind] = files = []; }
                    files.Add(relative);
                }
            }
        }
        Assert.Equal(PendingWidgetEmitters.Order(StringComparer.Ordinal), pending.Order(StringComparer.Ordinal)); // the list only shrinks.
        Assert.Contains("viewport", emitters.Keys); // the scan must see the Workspace's call site, or it is blind.
        var tableKinds = NodeContracts().Select(contract => VocabularyKind(contract.Kind)).ToHashSet();
        var stale = VocabularyKindsWithoutADartContract.Where(tableKinds.Contains).ToArray();
        Assert.True(stale.Length == 0, "VocabularyKindsWithoutADartContract lists kinds that now have a Dart entry: " + string.Join(", ", stale));
        var calls = emitters.Where(entry => entry.Value.Any(source => source != "UIVocabulary")).Select(entry => entry.Key);
        Assert.Empty(calls.Intersect(VocabularyKindsWithoutADartContract));
        var missing = emitters.Where(entry => !tableKinds.Contains(entry.Key) && !VocabularyKindsWithoutADartContract.Contains(entry.Key)).Select(entry => $"{entry.Key} (emitted by {string.Join(", ", entry.Value)})").ToArray();
        Assert.True(missing.Length == 0, "Kinds the server can emit have no entry in node_contracts.dart:\n" + string.Join('\n', missing));
    }

    [GeneratedRegex(@"UiChildRef\(\s*""(?<kind>\w+)""")]
    private static partial Regex LiteralUiChildRef();

    [GeneratedRegex(@"'(?<kind>\w+)':\s*NodeContract\(\s*'(?<contract>[\w.]+)',\s*'(?<changed>\w+)',?\s*\)")]
    private static partial Regex NodeContract();
}
