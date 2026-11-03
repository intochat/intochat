using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Tasks;

namespace IntoChat.OS.Apps.Ino;

internal static class InoIntentRouter
{
    public static readonly string[] Candidates =
    [
        "markdraw",
        "photocraft",
        "filmcraft",
        "chart.mutation",
        "chart.question",
        "database.query",
        "settings.navigate",
        "file.resource",
        "connection.string",
        "app.install",
        "task.create",
        "general.chat",
    ];

    public static async Task<DecisionResult> DecideIntentAsync(
        string text,
        IDecisionMaker? decisionMaker,
        Func<string, IApp?>? tryGetApp = null,
        Func<IAppStore?>? tryGetAppStore = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DecisionResult("general.chat", 1.0, DecisionMode.Reactive);
        }

        IReadOnlyList<DiscoveredAppCapability>? discoveredApps = null;
        if (tryGetApp is not null)
        {
            discoveredApps = await InoCapabilityCatalog.DiscoverAppsAsync(tryGetApp, tryGetAppStore);
        }

        var descriptions = InoCapabilityCatalog.BuildCandidateDescriptions(discoveredApps);

        if (decisionMaker is not null)
        {
            var context = new DecisionContext(
                text,
                Candidates,
                Mode: DecisionMode.Reactive,
                CandidateDescriptions: descriptions);
            var decision = await decisionMaker.DecideAsync(context);

            // Never allow capability/meta questions ("hey, what can you do?", "help", "who are you?") to trigger tool apps
            if (InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text))
            {
                return new DecisionResult("general.chat", 0.99, DecisionMode.Reactive);
            }

            if (InoFilesHandler.TryParseFileIntent(text, out _) && !InoStudioHandler.TryMatchExplicitStudioIntent(text, out _))
            {
                return new DecisionResult("file.resource", 0.98, DecisionMode.Reactive);
            }

            if (decision.Confidence < 0.8 || decision.Choice == "general.chat")
            {
                if (InoSettingsHandler.TryResolveSettingsNavigation(text, out _, out _, out _, out _))
                {
                    return new DecisionResult("settings.navigate", 0.95, DecisionMode.Reactive);
                }

                if (InoStudioHandler.TryMatchExplicitStudioIntent(text, out var studioApp))
                {
                    return new DecisionResult(studioApp, 0.95, DecisionMode.Reactive);
                }

                if (InoFilesHandler.TryParseFileIntent(text, out _))
                {
                    return new DecisionResult("file.resource", 0.95, DecisionMode.Reactive);
                }

                if (TryParseTaskIntent(text, out _, out _))
                {
                    return new DecisionResult("task.create", 0.95, DecisionMode.Reactive);
                }

                return new DecisionResult("general.chat", decision.Confidence, DecisionMode.Reactive);
            }

            return decision;
        }

        return ClassifyDeterministicFallback(text);
    }

    public static DecisionResult ClassifyDeterministicFallback(string text)
    {
        if (InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text))
        {
            return new DecisionResult("general.chat", 1.0, DecisionMode.Reactive);
        }

        if (text.Contains("intochat.app/a/", StringComparison.OrdinalIgnoreCase))
        {
            return new DecisionResult("app.install", 1.0, DecisionMode.Reactive);
        }

        if (InoDatabaseChartHandler.IsConnectionString(text))
        {
            return new DecisionResult("connection.string", 1.0, DecisionMode.Reactive);
        }

        if (InoStudioHandler.TryMatchExplicitStudioIntent(text, out var studioApp))
        {
            return new DecisionResult(studioApp, 1.0, DecisionMode.Reactive);
        }

        if (InoDatabaseChartHandler.IsChartMutationOrFilter(text))
        {
            return new DecisionResult("chart.mutation", 1.0, DecisionMode.Reactive);
        }

        if (InoDatabaseChartHandler.IsChartQuestion(text))
        {
            return new DecisionResult("chart.question", 1.0, DecisionMode.Reactive);
        }

        if (InoDatabaseChartHandler.IsDatabaseOrVisualizationQuery(text))
        {
            return new DecisionResult("database.query", 1.0, DecisionMode.Reactive);
        }

        if (InoSettingsHandler.TryResolveSettingsNavigation(text, out _, out _, out _, out _))
        {
            return new DecisionResult("settings.navigate", 1.0, DecisionMode.Reactive);
        }

        if (InoFilesHandler.TryParseFileIntent(text, out _))
        {
            return new DecisionResult("file.resource", 1.0, DecisionMode.Reactive);
        }

        if (TryParseTaskIntent(text, out _, out _))
        {
            return new DecisionResult("task.create", 1.0, DecisionMode.Reactive);
        }

        return new DecisionResult("general.chat", 0.5, DecisionMode.Reactive);
    }

    public static bool TryParseTaskIntent(string text, out string title, out TaskStepDefinition[] steps)
    {
        title = string.Empty;
        steps = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        string? body = null;
        string[] prefixes = ["create task:", "create task ", "start task:", "start task ", "run task:", "run task ", "new task:", "new task "];
        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                body = trimmed[prefix.Length..].Trim();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var rawSteps = body
            .Split(["->", ";", " then "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        title = rawSteps.Length > 0 ? rawSteps[0] : body;
        if (rawSteps.Length <= 1)
        {
            steps = [new TaskStepDefinition(title, body, InferCapability(body))];
            return true;
        }

        steps = rawSteps
            .Select(stepText => new TaskStepDefinition(stepText, stepText, InferCapability(stepText)))
            .ToArray();
        return true;
    }

    private static string InferCapability(string stepText)
    {
        var lower = stepText.ToLowerInvariant();
        if (lower.Contains("merge", StringComparison.Ordinal)) { return "github.merge"; }
        if (lower.Contains("rollout", StringComparison.Ordinal) || lower.Contains("deploy", StringComparison.Ordinal)) { return "cluster.rollout"; }
        if (lower.Contains("file", StringComparison.Ordinal)) { return "files.open"; }
        if (lower.Contains("note", StringComparison.Ordinal)) { return "notes.open"; }
        if (lower.Contains("setting", StringComparison.Ordinal)) { return "settings.open"; }
        return "app.execute";
    }
}
