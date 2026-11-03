using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Decision;

namespace IntoChat.OS.Apps.Ino;

internal static class InoIntentRouter
{
    public static readonly string[] Candidates =
    [
        "photocraft",
        "filmcraft",
        "chart.mutation",
        "chart.question",
        "database.query",
        "settings.navigate",
        "file.resource",
        "connection.string",
        "app.install",
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

                if (InoFilesHandler.TryParseFileIntent(text, out _))
                {
                    return new DecisionResult("file.resource", 0.95, DecisionMode.Reactive);
                }

                if (InoStudioHandler.TryMatchExplicitStudioIntent(text, out var studioApp))
                {
                    return new DecisionResult(studioApp, 0.95, DecisionMode.Reactive);
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

        return new DecisionResult("general.chat", 0.5, DecisionMode.Reactive);
    }
}
