namespace DigitalBrain.Launcher.Processes;

internal static class AspireRun
{
    public static IRunningProcess Start(IProcessRunner processes, string worktree, string appHostProject, Revisions.Revision revision)
        => processes.Start("aspire", ["run", "--non-interactive", "--project", Path.Combine(worktree, appHostProject)], worktree, new Dictionary<string, string>
        {
            ["Orleans__ServiceId"] = revision.ServiceId,
            ["Orleans__ClusterId"] = revision.ServiceId,
            ["DigitalBrain__Orleans__GrainStateContainer"] = revision.Container,
            ["DigitalBrain__Http__UiPort"] = revision.UiPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["DigitalBrain__Http__McpPort"] = revision.McpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // The launcher's own dashboard ports would collide with the current revision's.
            ["ASPNETCORE_URLS"] = $"http://localhost:{revision.UiPort + 2}",
            ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = $"http://localhost:{revision.UiPort + 3}",
            ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://localhost:{revision.UiPort + 4}",
        });
}
