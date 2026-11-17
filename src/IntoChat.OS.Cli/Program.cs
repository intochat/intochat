using DigitalBrain.Runtime.Orleans.Access;
using IntoChat.OS.Framework;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Modules.Apps.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A first-party host over the cluster client: it joins the local brain's gateway and acts as the app "cli" on behalf of a person.
// IntoChat.OS.Cli runs --gateway <port> --cluster <id> [--service os] [--brain owner] [--principal p] [--account a] [--limit 20]
// Aspire shows the gateway (the kernel's second tcp endpoint) and Orleans__ClusterId; each option also reads INTOCHAT_<NAME>.
var options = CliOptions.Parse(args);
if (options.Command != "runs")
{
    Console.Error.WriteLine("usage: IntoChat.OS.Cli runs --gateway <port> --cluster <id> [--service <id>] [--brain <id>] [--principal <id>] [--account <id>] [--limit <n>]");
    return 2;
}

using var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
    .UseOrleansClient(client => client.UseLocalhostClustering(options.Gateway, options.Service, options.Cluster).AddIntoChatOS())
    .Build();
await host.StartAsync();

var brains = host.Services.GetRequiredService<IIntoChatOSBrains>();
using (ClientCaller.ActAs("cli", options.Principal, options.Account, options.Brain))
{
    var runs = brains.Get<IRuns>($"{options.Brain}/{IRuns.Key(options.Brain).Key}");
    foreach (var run in await runs.List(options.Limit))
    {
        Console.WriteLine($"{run.State,-9} {run.Kind,-10} {run.Id,-40} {run.Title}{(run.Waiting is { } waiting ? $"  (waits: {waiting})" : "")}");
    }
}

await host.StopAsync();
return 0;

internal sealed record CliOptions(string Command, int Gateway, string Cluster, string Service, string Brain, string Principal, string Account, int Limit)
{
    internal static CliOptions Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : Environment.GetEnvironmentVariable("INTOCHAT_" + name.TrimStart('-').ToUpperInvariant()) ?? fallback;
        }
        var brain = Value("--brain", "owner");
        return new(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "", int.Parse(Value("--gateway", "30000"), System.Globalization.CultureInfo.InvariantCulture),
            Value("--cluster", "default"), Value("--service", "os"),
            brain, Value("--principal", brain), Value("--account", brain), int.Parse(Value("--limit", "20"), System.Globalization.CultureInfo.InvariantCulture));
    }
}
