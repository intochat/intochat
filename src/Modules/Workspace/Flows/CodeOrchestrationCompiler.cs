using System.Text;
using DigitalBrain;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.CodeBlocks;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>Result of validating, compiling, and (if needed) repairing a Plane 1 orchestration script.</summary>
internal sealed record CodeOrchestrationCompileOutcome(
    bool Succeeded,
    string ScriptSource,
    CompiledAppPackage? CompiledPackage,
    string[] RequiredContracts,
    string[] PersonOnlyCalls,
    int RepairRoundsUsed,
    string[] Diagnostics);

/// <summary>
/// Orchestrates C# orchestration script generation, Roslyn AST sanitization, <c>net11.0</c> compilation via
/// <see cref="AppPackageCompiler"/>, up to <see cref="MaxRepairRounds"/> compiler-feedback repair rounds,
/// and least-privilege contract + <see cref="PersonOnlyAttribute"/> inspection.
/// </summary>
internal sealed class CodeOrchestrationCompiler(
    AppPackageCompiler packageCompiler,
    ICodeOrchestrationAstSanitizer astSanitizer)
{
    public const int MaxRepairRounds = 2;

    public async Task<CodeOrchestrationCompileOutcome> CompileWithRepairAsync(
        BrainId brainId,
        string appKey,
        string title,
        string goal,
        string inputJson,
        DiscoveredNeuronContract[] availableContracts,
        IReadOnlyList<string>? allowedContracts,
        string? preauthoredScript,
        ICodeOrchestrationModel? model,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(availableContracts);

        var maxAttempts = !string.IsNullOrWhiteSpace(preauthoredScript) && model is null
            ? 1
            : MaxRepairRounds + 1;

        var astCatalog = ToAstDescriptors(availableContracts);
        var catalogPrompt = NeuronContractDiscovery.FormatPromptCatalog(availableContracts);
        var availableAliases = availableContracts.Select(static c => c.Alias).ToArray();

        var currentScript = string.Empty;
        string[] lastDiagnostics = [];

        for (var round = 0; round < maxAttempts; round++)
        {
            ct.ThrowIfCancellationRequested();

            if (round == 0 && !string.IsNullOrWhiteSpace(preauthoredScript))
            {
                currentScript = CodeOrchestrationScriptWrapper.StripCodeFences(preauthoredScript);
            }
            else
            {
                if (model is null)
                {
                    return new CodeOrchestrationCompileOutcome(
                        Succeeded: false,
                        ScriptSource: currentScript,
                        CompiledPackage: null,
                        RequiredContracts: [],
                        PersonOnlyCalls: [],
                        RepairRoundsUsed: round,
                        Diagnostics: lastDiagnostics.Length > 0
                            ? lastDiagnostics
                            : ["No ICodeOrchestrationModel is configured to generate or repair scripts."]);
                }

                var prompt = new CodeOrchestrationPrompt(
                    BrainId: brainId,
                    Goal: goal,
                    InputJson: inputJson,
                    ContractCatalogPrompt: catalogPrompt,
                    AvailableContractAliases: availableAliases,
                    PreviousScript: round == 0 ? null : currentScript,
                    PreviousDiagnostics: lastDiagnostics,
                    RepairRound: round);

                var generated = await model.GenerateScriptAsync(prompt, ct).ConfigureAwait(false);
                currentScript = CodeOrchestrationScriptWrapper.StripCodeFences(generated);
            }

            // 1. Roslyn AST sanitization BEFORE compilation.
            var ast = astSanitizer.Validate(currentScript);
            if (!ast.IsValid)
            {
                lastDiagnostics = ast.Diagnostics;
                continue;
            }

            // 2. Wrap into net11.0 sandbox AppPackage and compile with Roslyn + PE banned-API validation.
            var package = CodeOrchestrationScriptWrapper.BuildPackage(appKey, title, currentScript);
            var compiled = packageCompiler.Compile(package);
            if (!compiled.Succeeded || compiled.EnrichedPackage is null)
            {
                lastDiagnostics = compiled.Errors.Length > 0 ? compiled.Errors : ["Script compilation failed."];
                continue;
            }

            // 3. Infer least-privilege contracts from both compiled PE metadata and Roslyn AST.
            var astUsage = astSanitizer.InspectContractUsage(currentScript, astCatalog);
            var peNeeds = CodeBlockNeeds.Infer(compiled.AssemblyBytes);
            var combinedContracts = CombineContracts(peNeeds, astUsage.ReferencedContracts, availableContracts);

            // 4. Reject authority contracts unconditionally.
            var forbidden = CodeBlockNeeds.Forbidden(peNeeds)
                .Concat(combinedContracts.Where(NeuronContractDiscovery.IsAuthorityContract))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (forbidden.Length > 0)
            {
                lastDiagnostics =
                [
                    $"Script.cs(1): AUTH001: Orchestration script may not reference authority contract(s): {string.Join(", ", forbidden)}.",
                ];
                continue;
            }

            // 5. Enforce explicit allowedContracts scope when the caller constrained the flow.
            if (allowedContracts is { Count: > 0 })
            {
                var outOfScope = FindOutOfScopeContracts(combinedContracts, availableContracts, allowedContracts);
                if (outOfScope.Length > 0)
                {
                    lastDiagnostics =
                    [
                        $"Script.cs(1): SCOPE001: Script references contract(s) outside allowed scope [{string.Join(", ", allowedContracts)}]: {string.Join(", ", outOfScope)}.",
                    ];
                    continue;
                }
            }

            return new CodeOrchestrationCompileOutcome(
                Succeeded: true,
                ScriptSource: currentScript,
                CompiledPackage: compiled,
                RequiredContracts: combinedContracts,
                PersonOnlyCalls: astUsage.PersonOnlyCalls,
                RepairRoundsUsed: round,
                Diagnostics: []);
        }

        return new CodeOrchestrationCompileOutcome(
            Succeeded: false,
            ScriptSource: currentScript,
            CompiledPackage: null,
            RequiredContracts: [],
            PersonOnlyCalls: [],
            RepairRoundsUsed: Math.Min(maxAttempts - 1, MaxRepairRounds),
            Diagnostics: lastDiagnostics);
    }

    private static string[] CombineContracts(
        string[] peNeeds,
        string[] astReferences,
        DiscoveredNeuronContract[] catalog)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in peNeeds.Concat(astReferences))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var matched = catalog.FirstOrDefault(c =>
                string.Equals(c.FullName, raw, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.ShortName, raw, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Alias, raw, StringComparison.OrdinalIgnoreCase)
                || c.AllAliases.Contains(raw, StringComparer.OrdinalIgnoreCase));

            aliases.Add(matched is not null ? matched.Alias : raw.Trim());
        }

        return [.. aliases.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static string[] FindOutOfScopeContracts(
        string[] referenced,
        DiscoveredNeuronContract[] catalog,
        IReadOnlyList<string> allowedContracts)
    {
        var violations = new List<string>();
        foreach (var contractName in referenced)
        {
            var discovered = catalog.FirstOrDefault(c =>
                string.Equals(c.Alias, contractName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.FullName, contractName, StringComparison.OrdinalIgnoreCase)
                || c.AllAliases.Contains(contractName, StringComparer.OrdinalIgnoreCase));

            var allowed = discovered is not null
                ? discovered.AllAliases.Any(alias => NeuronContractDiscovery.IsContractAllowed(alias, null, allowedContracts))
                : NeuronContractDiscovery.IsContractAllowed(contractName, null, allowedContracts);

            if (!allowed)
            {
                violations.Add(contractName);
            }
        }

        return [.. violations];
    }

    private static AstContractDescriptor[] ToAstDescriptors(DiscoveredNeuronContract[] catalog)
        => catalog.Select(static c => new AstContractDescriptor(
            Alias: c.Alias,
            FullName: c.FullName,
            ShortName: c.ShortName,
            AllAliases: c.AllAliases,
            PersonOnlyMethods: c.Methods
                .Where(static m => m.RequiresPersonApproval)
                .Select(static m => m.Name)
                .ToArray())).ToArray();
}

/// <summary>
/// Production <see cref="ICodeOrchestrationModel"/> backed by <see cref="IModelInference"/>
/// and <see cref="ModelCatalog"/> (from <c>IntoChat.OS.Modules.AI</c>).
/// </summary>
internal sealed class ModelInferenceCodeOrchestrationModel(IServiceProvider services) : ICodeOrchestrationModel
{
    public async Task<string> GenerateScriptAsync(CodeOrchestrationPrompt prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var inference = services.GetService<IModelInference>()
            ?? throw new InvalidOperationException("IModelInference is not registered; cannot generate orchestration scripts.");

        var brainValue = prompt.BrainId.Value;
        ModelSelection? selection = null;
        if (services.GetService<ModelCatalog>() is { } catalog)
        {
            var resolved = await catalog.Decide(brainValue, ModelRole.Think, null, requiresTools: false, ct)
                .ConfigureAwait(false);
            selection = new ModelSelection(resolved.Model, resolved.Profile);
        }

        var messages = BuildMessages(prompt);
        var request = new InferenceRequest(messages, Model: selection);
        var response = await inference.Generate(request, model: null, cancellationToken: ct, brainId: brainValue)
            .ConfigureAwait(false);

        return string.Concat(response.Messages.SelectMany(static m => m.Content).OfType<AiText>().Select(static t => t.Text));
    }

    internal static AiMessage[] BuildMessages(CodeOrchestrationPrompt prompt)
    {
        var system = """
            You generate standalone C# (.NET 11 / net11.0) orchestration scripts for IntoChat OS Plane 1.
            Rules:
            - Define `public static class OrchestrationScript` with:
              `public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)`
            - Call cluster Neurons exclusively through typed proxies (`os.Get<TContract>(key)`, `os.Notifications`, `os.Ai`, `os.Tables`, `os.Folder(...)`).
            - Return `new OrchestrationResult(Status: "succeeded", Summary: "...", Artifacts: [...], Metrics: new Dictionary<string, double> { ... })`.
            - NEVER use System.Reflection, System.Net.Sockets, System.Net.Http, System.Diagnostics.Process, System.Runtime.InteropServices, System.Runtime.Loader, System.IO.File/Directory/StreamReader/StreamWriter, unsafe code, or Orleans APIs.
            - Reply ONLY with valid C# source code (no prose).
            """;

        var user = new StringBuilder();
        user.AppendLine("Goal:");
        user.AppendLine(prompt.Goal);
        user.AppendLine();
        user.AppendLine("Input JSON:");
        user.AppendLine(string.IsNullOrWhiteSpace(prompt.InputJson) ? "{}" : prompt.InputJson);
        user.AppendLine();
        user.AppendLine("Scoped Neuron Contracts:");
        user.AppendLine(string.IsNullOrWhiteSpace(prompt.ContractCatalogPrompt)
            ? "// No scoped Neuron contracts available."
            : prompt.ContractCatalogPrompt);

        if (prompt.RepairRound > 0 && prompt.PreviousDiagnostics.Length > 0)
        {
            user.AppendLine($"Repair Round {prompt.RepairRound}/{CodeOrchestrationCompiler.MaxRepairRounds}:");
            user.AppendLine("The previous script failed validation/compilation with these diagnostics:");
            foreach (var diag in prompt.PreviousDiagnostics)
            {
                user.AppendLine("- " + diag);
            }

            if (!string.IsNullOrWhiteSpace(prompt.PreviousScript))
            {
                user.AppendLine();
                user.AppendLine("Previous Script:");
                user.AppendLine(prompt.PreviousScript);
            }

            user.AppendLine();
            user.AppendLine("Fix every diagnostic above and return the complete corrected C# source file.");
        }

        return
        [
            new AiMessage("system", [new AiText(system)]),
            new AiMessage("user", [new AiText(user.ToString())]),
        ];
    }
}
