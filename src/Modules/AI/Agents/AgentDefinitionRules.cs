namespace IntoChat.OS.Modules.AI.Agents;

// Shared validation, defensive array normalization, and structural equality rules for AgentDefinition
// across both ConversationNeuron and the AgentNeuron compatibility adapter.
internal static class AgentDefinitionRules
{
    public static void Validate(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.MaxModelCalls is < 1 or > 512 || definition.Timeout <= TimeSpan.Zero || definition.Timeout > TimeSpan.FromMinutes(60)
            || definition.MaxHistoryMessages is < 2 or > 4096
            || definition.CompactionThreshold is < 2 or > 4096
            || definition.CompactionRecentWindow is < 1 or > 4096)
        {
            throw new ArgumentException("Invalid agent execution limits.", nameof(definition));
        }

        if (definition.Instructions.Length > 32_000 || definition.Contracts.Length > 128 || definition.Contracts.Any(string.IsNullOrWhiteSpace)
            || definition.Contracts.Distinct(StringComparer.Ordinal).Count() != definition.Contracts.Length
            || definition.Contexts.Length > 32 || definition.Contexts.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512)
            || (definition.OperationApps is { } operationApps
                && (operationApps.Length > 64
                    || operationApps.Any(app => string.IsNullOrWhiteSpace(app) || app.Length > 128)
                    || operationApps.Distinct(StringComparer.OrdinalIgnoreCase).Count() != operationApps.Length))
            || (definition.ToolSources is { } toolSources
                && (toolSources.Length > 64
                    || toolSources.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512)
                    || toolSources.Distinct(StringComparer.Ordinal).Count() != toolSources.Length)))
        {
            throw new ArgumentException("Instructions, contracts, operation apps, tool sources or contexts are invalid.", nameof(definition));
        }
    }

    public static AgentDefinition Normalize(AgentDefinition definition) => definition with
    {
        Contracts = [.. definition.Contracts],
        Capabilities = [.. definition.Capabilities],
        RoutingExamples = [.. definition.RoutingExamples],
        Contexts = [.. definition.Contexts],
        OperationApps = definition.OperationApps is null ? null : [.. definition.OperationApps],
        ToolSources = definition.ToolSources is null ? null : [.. definition.ToolSources],
    };

    public static bool AreEqual(AgentDefinition a, AgentDefinition b)
        => string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
            && string.Equals(a.Description, b.Description, StringComparison.Ordinal)
            && string.Equals(a.Instructions, b.Instructions, StringComparison.Ordinal)
            && Equals(a.Model, b.Model)
            && Equals(a.Options, b.Options)
            && a.MaxModelCalls == b.MaxModelCalls
            && a.Timeout == b.Timeout
            && a.MaxHistoryMessages == b.MaxHistoryMessages
            && a.CompactionThreshold == b.CompactionThreshold
            && a.CompactionRecentWindow == b.CompactionRecentWindow
            && a.UseModelSummarizer == b.UseModelSummarizer
            && a.IncludeComponentOperations == b.IncludeComponentOperations
            && a.Contracts.SequenceEqual(b.Contracts, StringComparer.Ordinal)
            && a.Capabilities.SequenceEqual(b.Capabilities, StringComparer.Ordinal)
            && a.RoutingExamples.SequenceEqual(b.RoutingExamples, StringComparer.Ordinal)
            && a.Contexts.SequenceEqual(b.Contexts, StringComparer.Ordinal)
            && SameOptionalSequence(a.OperationApps, b.OperationApps)
            && SameOptionalSequence(a.ToolSources, b.ToolSources);

    private static bool SameOptionalSequence(string[]? left, string[]? right)
        => (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => left.SequenceEqual(right, StringComparer.Ordinal),
        };
}
