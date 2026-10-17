using DigitalBrain.AI;
using DigitalBrain.AI.Inference;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Apps;

// The Builder's grounding: search the shared catalog for neuron contracts, read a module's contracts in
// full, and compile-check C# before answering. Framework owns the catalog; nothing here reflects.
internal static class BuilderTools
{
    public const string SearchContracts = "search_contracts";
    public const string ReadContracts = "read_contracts";
    public const string CheckCSharp = "check_csharp";

    public static readonly IReadOnlyList<InferenceTool> Definitions =
    [
        new(SearchContracts, "Search this brain's neuron contracts by what they do (for example \"schedule a timer\" or \"send an email\"). Returns matching contracts with their module id.",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        new(ReadContracts, "Read the full contracts of the given module ids: every neuron interface with its methods and signals; a C# file uses them with plain usings. Pass modules=[] to list the installed module ids.",
            """{"type":"object","properties":{"modules":{"type":"array","items":{"type":"string"}}},"required":["modules"]}"""),
        new(CheckCSharp, "Compile and store the given C# files (path to full source, for example notify.cs) without running them. Returns the compile errors; fix them all before answering.",
            """{"type":"object","properties":{"files":{"type":"object","additionalProperties":{"type":"string"}}},"required":["files"]}"""),
    ];

    public sealed record SearchArguments(string Query);
    public sealed record ReadArguments(IReadOnlyList<string>? Modules);
    public sealed record CheckArguments(IReadOnlyDictionary<string, string>? Files);

    public sealed record ContractHit(string Module, string Contract, string Description);

    public static async Task<IReadOnlyList<ContractHit>> Search(NeuronTypeSearch search, string query, CancellationToken ct)
        => [.. (await search.Search(query, ct: ct).ConfigureAwait(false)).Select(hit => new ContractHit(hit.Type.ModuleId, hit.Type.Contract, hit.Type.Description))];
}

