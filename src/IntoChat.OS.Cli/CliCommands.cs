using System.Net.Http.Json;
using DigitalBrain;
using Grpc.Core;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Modules.Apps.AppRuns;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Runs;

namespace IntoChat.OS.Cli;

public static class CliCommands
{
    public const string Usage = "usage: IntoChat.OS.Cli runs | send <app> <command> [json] | start <workflow> [input] | cancel <run> [reason] | approve <plan> | deny <plan> | link <server> <code> | link status | unlink  [--base url] [--brain id] [--limit n]";

    // handler replaces the HTTP stack (a test's in-process server); null uses the machine's.
    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error, Func<string, string?> environment, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        CliOptions options;
        try { options = CliOptions.Parse(args, environment); }
        catch (ArgumentException invalid)
        {
            await error.WriteLineAsync(invalid.Message);
            return 2;
        }
        try
        {
            return options.Verb switch
            {
                "approve" or "deny" => await Answer(options, options.Verb == "approve", output, error, handler),
                "link" => await LinkAsync(options, output, error, handler),
                "unlink" => await UnlinkAsync(options, output, error, handler),
                "runs" or "send" or "start" or "cancel" => await OverTheEdge(options, output, error, handler),
                _ => await Fail(error, Usage, 2),
            };
        }
        catch (RpcException refused)
        {
            await error.WriteLineAsync($"{options.Verb}: the kernel answered {refused.StatusCode}: {refused.Status.Detail}{Hint(refused.StatusCode, options)}");
            return 1;
        }
        catch (HttpRequestException unreachable)
        {
            await error.WriteLineAsync($"{options.Verb}: cannot reach the kernel at {options.Base}: {unreachable.Message}");
            return 1;
        }
    }

    private static string Hint(StatusCode code, CliOptions options) => code == StatusCode.Unauthenticated && options.Proof is null
        ? $" (set {CliProof.PresenceVariable} on this machine, or {CliProof.BasicVariable} / {CliProof.SessionVariable} for a remote install)"
        : "";

    private static async Task<int> OverTheEdge(CliOptions options, TextWriter output, TextWriter error, HttpMessageHandler? handler)
    {
        using var edge = new NeuronsEdge(options.Base, options.Brain, options.Proof, handler);
        var brain = options.Brain;
        switch (options.Verb)
        {
            case "runs":
                foreach (var run in await edge.Call(typeof(IRuns), IRuns.Key(brain).FullKey(), nameof(IRuns.List),
                    new ListRunsArgs(options.Limit), CliJsonContext.Default.ListRunsArgs, CliJsonContext.Default.RunViewArray) ?? [])
                {
                    await output.WriteLineAsync($"{run.State,-9} {run.Kind,-10} {run.Id,-48} {run.Title}{(run.Waiting is { } waiting ? $"  (waits: {waiting})" : "")}");
                }
                return 0;
            case "send" when options.Arguments is [var app, var command, ..]:
                var sent = await edge.Call(typeof(IAppRun), new NeuronId(new BrainId(brain), "app.run", $"commands/{app}/{Guid.NewGuid():N}").FullKey(), nameof(IAppRun.Start),
                    new StartAppRunArgs(AppTrigger.Command, app, command, options.Arguments.ElementAtOrDefault(2)),
                    CliJsonContext.Default.StartAppRunArgs, CliJsonContext.Default.AppRunView)
                    ?? throw new InvalidOperationException("IAppRun.Start returned nothing.");
                await output.WriteLineAsync($"{sent.Status} {sent.RunId}{(sent.Error is { } failure ? $"  {failure}" : sent.ResultJson is { } result ? $"  {result}" : "")}");
                return sent.Status == AppRunStatus.Failed ? 1 : 0;
            case "start" when options.Arguments is [var workflow, ..]:
                var started = await edge.Call(typeof(IWorkflowRun), new NeuronId(new BrainId(brain), "workflow.run", $"workflows/cli-{Guid.NewGuid().ToString("N")[..12]}").FullKey(), nameof(IWorkflowRun.Start),
                    new StartWorkflowArgs(workflow, options.Arguments.ElementAtOrDefault(1) ?? ""),
                    CliJsonContext.Default.StartWorkflowArgs, CliJsonContext.Default.WorkflowRunView)
                    ?? throw new InvalidOperationException("IWorkflowRun.Start returned nothing.");
                await output.WriteLineAsync($"{started.Status} {started.RunId}{(started.PlanId is { } plan ? $"  (waits: approval {plan})" : "")}");
                return started.Status == WorkflowRunStatus.Failed ? 1 : 0;
            case "cancel" when options.Arguments is [var runId, ..]:
                var reason = new CancelRunArgs(options.Arguments.ElementAtOrDefault(1) ?? "Cancelled from the command line.");
                string? outcome = runId.StartsWith("workflows/", StringComparison.Ordinal)
                    ? (await edge.Call(typeof(IWorkflowRun), new NeuronId(new BrainId(brain), "workflow.run", runId).FullKey(), nameof(IWorkflowRun.Cancel),
                        reason, CliJsonContext.Default.CancelRunArgs, CliJsonContext.Default.WorkflowRunView))?.Status.ToString()
                    : runId.StartsWith("code-orchestration/", StringComparison.Ordinal)
                        ? (await edge.Call(typeof(ICodeOrchestration), new NeuronId(new BrainId(brain), "flow.code-orchestration", runId).FullKey(), nameof(ICodeOrchestration.CancelAsync),
                            reason, CliJsonContext.Default.CancelRunArgs, CliJsonContext.Default.CodeOrchestrationView))?.Status.ToString()
                        : null;
                await output.WriteLineAsync(outcome is null ? $"No cancellable run {runId}: workflows/ and code-orchestration/ runs can be cancelled." : $"{outcome} {runId}");
                return outcome is null ? 1 : 0;
            default:
                return await Fail(error, $"{options.Verb}: missing arguments.", 2);
        }
    }

    // A confirmation is answered by the person, not the app: plain HTTP to the edge with the proof alone.
    private static async Task<int> Answer(CliOptions options, bool approve, TextWriter output, TextWriter error, HttpMessageHandler? handler)
    {
        if (options.Arguments is not [var plan, ..]) { return await Fail(error, $"{options.Verb}: which plan id?", 2); }
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = options.Base;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/brains/{Uri.EscapeDataString(options.Brain)}/confirmations/{string.Join('/', plan.Split('/').Select(Uri.EscapeDataString))}")
        {
            Content = JsonContent.Create(new ApproveConfirmationArgs(approve), CliJsonContext.Default.ApproveConfirmationArgs),
        };
        foreach (var (name, value) in options.Proof?.Headers() ?? []) { request.Headers.TryAddWithoutValidation(name, value); }
        request.Headers.Add("Origin", options.Base.GetLeftPart(UriPartial.Authority));
        using var response = await http.SendAsync(request);
        await output.WriteLineAsync($"{(int)response.StatusCode} {(approve ? "approved" : "denied")} {plan}");
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<int> LinkAsync(CliOptions options, TextWriter output, TextWriter error, HttpMessageHandler? handler)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = options.Base;

        if (options.Arguments is ["status"])
        {
            using var statusReq = CreateEdgeRequest(HttpMethod.Get, $"/brains/{Uri.EscapeDataString(options.Brain)}/devices/link/status", options);
            using var statusResp = await http.SendAsync(statusReq);
            var statusBody = await statusResp.Content.ReadAsStringAsync();
            if (!statusResp.IsSuccessStatusCode)
            {
                await error.WriteLineAsync($"link status: {(int)statusResp.StatusCode} {statusBody}".Trim());
                return 1;
            }

            await output.WriteLineAsync(statusBody);
            return 0;
        }

        HttpContent payload;
        if (options.Arguments is [var singleUri] && singleUri.StartsWith("intochat://", StringComparison.OrdinalIgnoreCase))
        {
            payload = JsonContent.Create(new LinkUriArgs(singleUri), CliJsonContext.Default.LinkUriArgs);
        }
        else if (options.Arguments is [var server, var code])
        {
            payload = JsonContent.Create(new LinkServerCodeArgs(server, code), CliJsonContext.Default.LinkServerCodeArgs);
        }
        else
        {
            return await Fail(error, "usage: link <server> <code> | link <intochat://enroll?...> | link status", 2);
        }

        using var req = CreateEdgeRequest(HttpMethod.Post, $"/brains/{Uri.EscapeDataString(options.Brain)}/devices/link/enroll", options);
        req.Content = payload;
        using var response = await http.SendAsync(req);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            await error.WriteLineAsync($"link: {(int)response.StatusCode} {body}".Trim());
            return 1;
        }

        await output.WriteLineAsync(body);
        return 0;
    }

    private static async Task<int> UnlinkAsync(CliOptions options, TextWriter output, TextWriter error, HttpMessageHandler? handler)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = options.Base;
        using var req = CreateEdgeRequest(HttpMethod.Post, $"/brains/{Uri.EscapeDataString(options.Brain)}/devices/link/leave", options);
        using var response = await http.SendAsync(req);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            await error.WriteLineAsync($"unlink: {(int)response.StatusCode} {body}".Trim());
            return 1;
        }

        await output.WriteLineAsync("Unlinked this device from IntoChat Cloud.");
        return 0;
    }

    private static HttpRequestMessage CreateEdgeRequest(HttpMethod method, string relativePath, CliOptions options)
    {
        var request = new HttpRequestMessage(method, relativePath);
        foreach (var (name, value) in options.Proof?.Headers() ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.Add("Origin", options.Base.GetLeftPart(UriPartial.Authority));
        return request;
    }

    private static async Task<int> Fail(TextWriter error, string message, int code)
    {
        await error.WriteLineAsync(message);
        return code;
    }
}
