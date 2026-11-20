using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed partial class InoNativeHarness
{
    private static string BuildSelfRepairPrompt(
        string originalSlicePrompt,
        EngineerTaskSlice slice,
        int attempt,
        string diagnosticContext)
    {
        var ownedPathsNote = slice.OwnedPaths is { Length: > 0 }
            ? $"Strictly confine repair edits to owned slice path(s): {string.Join(", ", slice.OwnedPaths)}."
            : "Strictly confine repair edits within repository policy.";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{originalSlicePrompt}\n\n[Intra-Slice Roslyn Self-Repair — Attempt {attempt}/{MaxSelfRepairTurns}]\n{ownedPathsNote}\n{diagnosticContext}\nFix the reported C# compiler error(s) so semantic/syntax diagnostics reach 0 errors.");
    }

    private static string BuildSlicePrompt(
        EngineeringHarnessRequest request,
        RatifiedPlan plan,
        EngineerTaskSlice slice,
        int totalSlices,
        string symbolHint,
        string ownershipDirective,
        string strategyDirective,
        string memoryDirective)
    {
        if (totalSlices == 1
            && request.TotalInstances == 1
            && string.IsNullOrWhiteSpace(memoryDirective)
            && (slice.OwnedPaths is null || slice.OwnedPaths.Length == 0))
        {
            return request.Goal;
        }

        if (totalSlices == 1
            && string.IsNullOrWhiteSpace(memoryDirective)
            && (slice.OwnedPaths is null || slice.OwnedPaths.Length == 0))
        {
            return $"{request.Goal}{strategyDirective}";
        }

        return $"{request.Goal}\n\n[Task {slice.SliceId}: {slice.Title}]\n{slice.Instructions}\nArchitect note: {plan.ArchitectureSummary}{ownershipDirective}{symbolHint}{strategyDirective}{memoryDirective}";
    }

    private bool CanResolveModelInference()
        => services?.GetService<IModelInference>() is not null;

    private async Task<CoderEditResult> RunNativeSliceModelWorkerAsync(
        EngineeringHarnessRequest request,
        EngineerTaskSlice slice,
        string agentKey,
        string slicePrompt,
        string hydratedContext,
        CancellationToken ct)
    {
        var (inference, selection, modelId, supportsTemp) = await ResolveSliceInferenceAsync(request.Brain, ct).ConfigureAwait(false);
        var slicePolicy = slice.OwnedPaths is { Length: > 0 } && slice.OwnedPaths.Length > _orchestrator.Policy.MaxChangedFiles
            ? new ChangePolicy(
                maxChangedFiles: slice.OwnedPaths.Length,
                allowedExtensions: _orchestrator.Policy.AllowedExtensions,
                blockedFileNames: _orchestrator.Policy.BlockedFileNames)
            : _orchestrator.Policy;

        var sandbox = new LocalLlmCoder.ConfinedWorkTreeSandbox(request.WorktreePath, slicePolicy, _orchestrator.Roslyn);
        var tools = BuildSliceTools(slicePolicy);
        var ownedSummary = slice.OwnedPaths is { Length: > 0 }
            ? $"Strictly confine all writes to your owned slice paths: {string.Join(", ", slice.OwnedPaths)}."
            : "Confine all writes within the repository policy.";
        var memorySection = string.IsNullOrWhiteSpace(hydratedContext)
            ? string.Empty
            : $"\nShared Hydrated Brain Context (<3KB):\n{hydratedContext}\n";

        var messages = new List<AiMessage>
        {
            new("system",
            [
                new AiText($"""
                    You are {agentKey}, an autonomous parallel AgentNeuron slice worker inside InoNativeHarness.
                    {slicePolicy.Describe()}
                    {ownedSummary}
                    {memorySection}
                    Prefer roslyn_batch_edit or roslyn_edit_member + roslyn_diagnostics for AST-safe C# edits, and write_file / replace_in_file for other allowed files.
                    Once your slice's files are updated and verified, respond with a concise 1-sentence summary.
                    """),
            ]),
            new("user", [new AiText(slicePrompt)]),
        };

        var lastSummary = $"[{agentKey}] Implemented slice '{slice.Title}'.";
        for (var round = 0; round < 10; round++)
        {
            ct.ThrowIfCancellationRequested();
            var options = supportsTemp ? new InferenceOptions(Temperature: 0.1f) : null;
            var reply = await inference.Generate(
                new InferenceRequest(messages, Model: selection, Options: options, Tools: tools),
                marker: null,
                cancellationToken: ct,
                brainId: request.Brain?.Value).ConfigureAwait(false);

            var assistant = reply.Messages.LastOrDefault(static m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                ?? reply.Messages.LastOrDefault()
                ?? new AiMessage("assistant", []);
            messages.Add(assistant);

            var calls = assistant.Content.OfType<AiToolCall>().ToArray();
            var text = string.Join("\n", assistant.Content.OfType<AiText>().Select(static t => t.Text)).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                lastSummary = text;
            }

            if (calls.Length == 0)
            {
                if (sandbox.TouchedFiles.Count > 0)
                {
                    foreach (var touched in sandbox.TouchedFiles)
                    {
                        InoCodeOrchestrator.EnsureWithinSliceOwnership(touched, slice.OwnedPaths);
                    }

                    return new CoderEditResult(lastSummary, sandbox.TouchedFiles, modelId);
                }

                messages.Add(new AiMessage("user", [new AiText("Apply the requested slice modification now using roslyn_batch_edit, roslyn_edit_member, replace_in_file, or write_file.")]));
                continue;
            }

            var toolResults = new List<AiContent>(calls.Length);
            foreach (var call in calls)
            {
                var output = await ExecuteSliceToolCallAsync(call, sandbox, request.WorktreePath, slice.OwnedPaths, ct).ConfigureAwait(false);
                toolResults.Add(new AiToolResult(call.CallId, LocalLlmCoder.EnsureJsonToolResult(output)));
            }

            messages.Add(new AiMessage("tool", [.. toolResults]));
        }

        if (sandbox.TouchedFiles.Count > 0)
        {
            foreach (var touched in sandbox.TouchedFiles)
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(touched, slice.OwnedPaths);
            }

            return new CoderEditResult(lastSummary, sandbox.TouchedFiles, modelId);
        }

        throw new InvalidOperationException($"Slice worker '{agentKey}' completed tool rounds without modifying any file for slice '{slice.SliceId}'.");
    }

    private async Task<string> ExecuteSliceToolCallAsync(
        AiToolCall call,
        LocalLlmCoder.ConfinedWorkTreeSandbox sandbox,
        string worktreePath,
        string[]? sliceOwnedPaths,
        CancellationToken ct)
    {
        if (string.Equals(call.Name, "roslyn_batch_edit", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                if (!doc.RootElement.TryGetProperty("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
                {
                    return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, "roslyn_batch_edit requires an 'edits' array."), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload);
                }

                var edits = new List<WorktreeCSharpEdit>();
                foreach (var item in editsEl.EnumerateArray())
                {
                    var rawKind = item.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "";
                    var rawPath = item.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "";
                    var kind = rawKind.Trim().ToLowerInvariant() switch
                    {
                        "add_using" or "addusing" => WorktreeCSharpEditKind.AddUsing,
                        "add_or_replace_member" or "addorreplacemember" => WorktreeCSharpEditKind.AddOrReplaceMember,
                        "remove_member" or "removemember" => WorktreeCSharpEditKind.RemoveMember,
                        "create_type_file" or "createtypefile" => WorktreeCSharpEditKind.CreateTypeFile,
                        _ => throw new ArgumentException($"Unsupported batch edit kind '{rawKind}'."),
                    };

                    edits.Add(new WorktreeCSharpEdit(
                        Kind: kind,
                        RelativePath: rawPath,
                        TypeName: item.TryGetProperty("type_name", out var tn) ? tn.GetString() : null,
                        MemberName: item.TryGetProperty("member_name", out var mn) ? mn.GetString() : null,
                        NamespaceName: item.TryGetProperty("namespace_name", out var nn) ? nn.GetString() : null,
                        Source: item.TryGetProperty("source", out var src) ? src.GetString() : null,
                        TypeKind: item.TryGetProperty("type_kind", out var tk) ? tk.GetString() : null,
                        BaseTypes: item.TryGetProperty("base_types", out var bt) ? bt.GetString() : null));
                }

                var summary = await _orchestrator.ApplyAstEditsAsync(worktreePath, edits, sliceOwnedPaths, ct).ConfigureAwait(false);
                sandbox.RecordTouchedFiles(summary.TouchedFiles);

                return JsonSerializer.Serialize(
                    new RoslynBatchResultPayload(summary.Succeeded, summary.TouchedFiles, summary.Diagnostics, summary.Detail),
                    CatalogAotJsonContext.Relaxed.RoslynBatchResultPayload);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, "roslyn_batch_edit failed");
                return JsonSerializer.Serialize(new RoslynBatchErrorPayload(false, ex.Message), CatalogAotJsonContext.Relaxed.RoslynBatchErrorPayload);
            }
        }

        if (call.Name is "write_file" or "replace_in_file" or "roslyn_edit_member")
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
            {
                InoCodeOrchestrator.EnsureWithinSliceOwnership(pathEl.GetString() ?? string.Empty, sliceOwnedPaths);
            }
        }

        return await sandbox.ExecuteToolAsync(call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
    }

    private async Task<(IModelInference Inference, ModelSelection? Selection, string ModelId, bool SupportsTemp)> ResolveSliceInferenceAsync(
        BrainId? brain,
        CancellationToken ct)
    {
        var inference = services?.GetService<IModelInference>()
            ?? throw new InvalidOperationException("IModelInference is not registered.");
        var catalog = services?.GetService<ModelCatalog>();

        if (catalog is not null)
        {
            var resolved = await catalog.Decide(brain?.Value, ModelUse.Code, null, personModel: null, requiresTools: true, ct).ConfigureAwait(false);
            if (resolved.Provider == AiProvider.Host && services?.GetService<IChatClient>() is null)
            {
                throw new NoProviderConfiguredException("No tool-capable model is configured for InoNativeHarness.");
            }

            ModelSelection? sel = resolved.Provider == AiProvider.Host ? null
                : resolved.Profile is { Length: > 0 } ? new($"profile:{resolved.Profile}", resolved.Reasoning, resolved.MaxOutputTokens)
                : resolved.Provider == AiProvider.Ollama ? new($"ollama/{resolved.Model}", resolved.Reasoning, resolved.MaxOutputTokens)
                : new(resolved.Model, resolved.Reasoning, resolved.MaxOutputTokens);
            var desc = await inference.Describe(sel, marker: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
            return (inference, sel, resolved.Model, desc.Temperature == CapabilitySupport.Supported);
        }

        var defaultDesc = await inference.Describe(null, marker: null, cancellationToken: ct, brainId: brain?.Value).ConfigureAwait(false);
        return (inference, null, defaultDesc.Model, defaultDesc.Temperature == CapabilitySupport.Supported);
    }

    private static IReadOnlyList<InferenceTool> BuildSliceTools(ChangePolicy policy)
    {
        var extensions = string.Join(", ", policy.AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase));
        return
        [
            new(
                "list_files",
                "List allowed repository files under a relative directory.",
                """{"type":"object","properties":{"directory":{"type":"string"},"filter":{"type":"string"}}}"""),
            new(
                "read_file",
                "Read the UTF-8 contents of a file inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
            new(
                "replace_in_file",
                "Replace an exact target snippet in an owned file inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"]}"""),
            new(
                "write_file",
                $"Create or overwrite an owned file ({extensions}) inside the worktree.",
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}"""),
            new(
                "roslyn_symbols",
                "Inspect C# types, namespaces, and members in the worktree via Roslyn.",
                """{"type":"object","properties":{"filter":{"type":"string"}}}"""),
            new(
                "roslyn_diagnostics",
                "Run Roslyn semantic diagnostics on worktree C# files.",
                """{"type":"object","properties":{"path":{"type":"string"}}}"""),
            new(
                "roslyn_edit_member",
                "Apply a single AST-validated C# edit (add_using, add_or_replace_member, remove_member, create_type_file).",
                """{"type":"object","properties":{"kind":{"type":"string"},"path":{"type":"string"},"type_name":{"type":"string"},"member_name":{"type":"string"},"namespace_name":{"type":"string"},"source":{"type":"string"},"type_kind":{"type":"string"},"base_types":{"type":"string"}},"required":["kind","path"]}"""),
            new(
                "roslyn_batch_edit",
                "Execute a deterministic multi-edit C# AST batch via InoCodeOrchestrator in a single turn.",
                """{"type":"object","properties":{"edits":{"type":"array","items":{"type":"object","properties":{"kind":{"type":"string"},"path":{"type":"string"},"type_name":{"type":"string"},"member_name":{"type":"string"},"namespace_name":{"type":"string"},"source":{"type":"string"},"type_kind":{"type":"string"},"base_types":{"type":"string"}},"required":["kind","path"]}}},"required":["edits"]}"""),
        ];
    }

}
