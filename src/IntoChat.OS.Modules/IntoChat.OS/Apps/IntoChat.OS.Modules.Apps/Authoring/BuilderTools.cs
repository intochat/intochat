using IntoChat.OS.AI;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Apps;

// The Builder's grounding: search the shared catalog for neuron contracts, read a module's contracts in
// full, and compile-check C# before answering. Framework owns the catalog; nothing here reflects.
internal static class BuilderTools
{
    public const string SearchContracts = "search_contracts";
    public const string ReadContracts = "read_contracts";
    public const string ReadContractDetail = "read_contract_detail";
    public const string CheckCSharp = "check_csharp";

    public static readonly IReadOnlyList<InferenceTool> Definitions =
    [
        new(SearchContracts, "Search this brain's neuron contracts by what they do (for example \"schedule a timer\" or \"send an email\"). Returns matching contracts with their module id.",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        new(ReadContracts, "Read bounded complete method and DTO declarations for the given module ids. Pass modules=[] to list installed ids. When Truncated is true, fetch omitted selectors with read_contract_detail before using them.",
            """{"type":"object","properties":{"modules":{"type":"array","items":{"type":"string"}}},"required":["modules"]}"""),
        new(ReadContractDetail, "Read an installed contract's complete request/result declarations. Use exact moduleId and contractName; pass methodName=null for the contract or one method name to narrow a truncated response. Never use an omitted schema.",
            """{"type":"object","properties":{"moduleId":{"type":"string"},"contractName":{"type":"string"},"methodName":{"type":["string","null"]}},"required":["moduleId","contractName","methodName"]}"""),
        new(CheckCSharp, "Compile and store the given C# files (path to full source, for example notify.cs) without running them. Returns the compile errors; fix them all before answering.",
            """{"type":"object","properties":{"files":{"type":"object","additionalProperties":{"type":"string"}}},"required":["files"]}"""),
    ];

    public sealed record SearchArguments(string Query);
    public sealed record ReadArguments(IReadOnlyList<string>? Modules);
    public sealed record DetailArguments(string ModuleId, string ContractName, string? MethodName);
    public sealed record CheckArguments(IReadOnlyDictionary<string, string>? Files);

    public sealed record ContractHit(string Module, string Contract, string Description);

    public static async Task<IReadOnlyList<ContractHit>> Search(NeuronTypeSearch search, string query, CancellationToken ct)
        => [.. (await search.Search(query, ct: ct).ConfigureAwait(false)).Select(hit => new ContractHit(hit.Type.ModuleId, hit.Type.Contract, hit.Type.Description))];
}

