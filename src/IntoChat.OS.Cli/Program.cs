using System.Net.Http.Json;
using DigitalBrain.Runtime.Orleans.Access;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Modules.Apps.AppRuns;
using IntoChat.OS.Modules.Apps.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A first-party host over the cluster client: it joins the local brain's gateway and acts as the app "cli" on behalf of a person.
//   runs                          list the runs index
//   send <app> <command> [json]   send a command to an installed sandbox app (one app run)
//   start <workflow> [input]      start a workflow run
//   approve|deny <plan id>        answer a confirmation as the person at this machine
// Gateway options: --gateway <port> --cluster <id> [--service os] [--brain owner] [--principal p] [--account a] [--limit 20].
// Aspire shows the gateway (the kernel's second tcp endpoint) and Orleans__ClusterId; each option also reads INTOCHAT_<NAME>.
// Answering a confirmation is a person's act, so it does not travel as the app: it goes to the kernel's HTTP edge (--base, default
// http://localhost:5080) with the local presence token (INTOCHAT_PRESENCE), which proves a person at this machine.
var options = CliOptions.Parse(args);
switch (options.Verb)
{
    case "approve" or "deny":
        return await Answer(options, options.Verb == "approve");
    case "runs" or "send" or "start":
        return await OverGateway(options);
    default:
        Console.Error.WriteLine("usage: IntoChat.OS.Cli runs | send <app> <command> [json] | start <workflow> [input] | approve <plan> | deny <plan>  [options]");
        return 2;
}

static async Task<int> OverGateway(CliOptions options)
{
    using var host = Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
        .UseOrleansClient(client => client.UseLocalhostClustering(options.Gateway, options.Service, options.Cluster).AddIntoChatOS())
        .Build();
    await host.StartAsync();
    try
    {
        var brains = host.Services.GetRequiredService<IIntoChatOSBrains>();
        using (ClientCaller.ActAs("cli", options.Principal, options.Account, options.Brain))
        {
            switch (options.Verb)
            {
                case "runs":
                    foreach (var run in await brains.Get<IRuns>($"{options.Brain}/{IRuns.Key(options.Brain).Key}").List(options.Limit))
                    {
                        Console.WriteLine($"{run.State,-9} {run.Kind,-10} {run.Id,-48} {run.Title}{(run.Waiting is { } waiting ? $"  (waits: {waiting})" : "")}");
                    }
                    return 0;
                case "send" when options.Arguments is [var app, var command, ..]:
                    var sent = await brains.Get<IAppRun>($"{options.Brain}/commands/{app}/{Guid.NewGuid():N}")
                        .Start(AppTrigger.Command, app, command, options.Arguments.ElementAtOrDefault(2));
                    Console.WriteLine($"{sent.Status} {sent.RunId}{(sent.Error is { } error ? $"  {error}" : sent.ResultJson is { } result ? $"  {result}" : "")}");
                    return sent.Status == AppRunStatus.Failed ? 1 : 0;
                case "start" when options.Arguments is [var workflow, ..]:
                    var started = await brains.Get<IWorkflowRun>($"{options.Brain}/workflows/cli-{Guid.NewGuid().ToString("N")[..12]}")
                        .Start(workflow, options.Arguments.ElementAtOrDefault(1) ?? "");
                    Console.WriteLine($"{started.Status} {started.RunId}{(started.PlanId is { } plan ? $"  (waits: approval {plan})" : "")}");
                    return started.Status == WorkflowRunStatus.Failed ? 1 : 0;
                default:
                    Console.Error.WriteLine($"{options.Verb}: missing arguments.");
                    return 2;
            }
        }
    }
    finally
    {
        await host.StopAsync();
    }
}

static async Task<int> Answer(CliOptions options, bool approve)
{
    if (options.Arguments is not [var plan, ..]) { Console.Error.WriteLine($"{options.Verb}: which plan id?"); return 2; }
    if (Environment.GetEnvironmentVariable("INTOCHAT_PRESENCE") is not { Length: > 0 } presence)
    {
        Console.Error.WriteLine("Answering needs the local presence token in INTOCHAT_PRESENCE.");
        return 2;
    }
    using var http = new HttpClient { BaseAddress = new Uri(options.Base) };
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/brains/{options.Brain}/apps/event")
    {
        Content = JsonContent.Create(new { kind = "confirmation", name = $"{options.Brain}/confirmations", planId = plan, approve }),
    };
    request.Headers.Add("X-IntoChat-Presence", presence);
    request.Headers.Add("Origin", options.Base);
    using var response = await http.SendAsync(request);
    Console.WriteLine($"{(int)response.StatusCode} {(approve ? "approved" : "denied")} {plan}");
    return response.IsSuccessStatusCode ? 0 : 1;
}

internal sealed record CliOptions(string Verb, string[] Arguments, int Gateway, string Cluster, string Service, string Brain, string Principal, string Account, int Limit, string Base)
{
    private static readonly string[] Named = ["--gateway", "--cluster", "--service", "--brain", "--principal", "--account", "--limit", "--base"];

    internal static CliOptions Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : Environment.GetEnvironmentVariable("INTOCHAT_" + name.TrimStart('-').ToUpperInvariant()) ?? fallback;
        }
        var positional = args.Where((arg, i) => !Named.Contains(arg) && (i == 0 || !Named.Contains(args[i - 1]))).ToArray();
        var brain = Value("--brain", "owner");
        var number = System.Globalization.CultureInfo.InvariantCulture;
        return new(positional.FirstOrDefault() ?? "", [.. positional.Skip(1)], int.Parse(Value("--gateway", "30000"), number),
            Value("--cluster", "default"), Value("--service", "os"), brain, Value("--principal", brain), Value("--account", brain),
            int.Parse(Value("--limit", "20"), number), Value("--base", "http://localhost:5080"));
    }
}
