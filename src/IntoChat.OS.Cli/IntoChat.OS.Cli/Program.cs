using System.Diagnostics;

namespace IntoChat.OS.Cli;

internal static class Program
{
    private static void LogFailure(Exception error) => Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");

    private static readonly HashSet<string> LauncherProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "default", "headless", "local", "preflight", "reset"
    };

    private static string? FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "intochat.cs")) &&
                File.Exists(Path.Combine(current.FullName, "src", "IntoChat.OS.AppHost", "IntoChat.OS.AppHost.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    private static async Task<int> LaunchOS(string repoRoot, string[] args, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(repoRoot, "intochat.cs");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(scriptPath);
        if (args.Length > 0)
        {
            start.ArgumentList.Add("--");
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {scriptPath}.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        void Handler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); }
        Console.CancelKeyPress += Handler;
        try
        {
            if (args.Any(argument => argument is "--token"))
            { Console.Error.WriteLine("--token is not accepted. The dev token is read from the local OS."); return 2; }
            if (args is ["__extract-app-metadata", var assembly, var destination])
            { await BuildMetadata.Extract(assembly, destination, cancellation.Token).ConfigureAwait(false); return 0; }

            var repoRoot = FindRepoRoot();
            if (repoRoot is not null)
            {
                if (args.Length == 0 || LauncherProfiles.Contains(args[0]) || args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return await LaunchOS(repoRoot, args, cancellation.Token).ConfigureAwait(false);
                }
            }

            var command = AppCommand.Parse(args);
            switch (command.Kind)
            {
                case AppCommandKind.Help:
                    Console.WriteLine("intochat [default|headless|local|preflight|reset|<config>.json]\nintochat pack <project.csproj> -o <app.intochat-app>\nintochat run <project-or-folder> [--endpoint <url>] [--brain <id>]\nintochat install <package> [--endpoint <url>] [--brain <id>]");
                    return 0;
                case AppCommandKind.Run when Directory.Exists(command.Target):
                    return await FolderRun.Execute(command, Console.Error, Console.Out, (push, token) => FolderRun.Post(command.Endpoint ?? FolderRun.DefaultEndpoint, FolderRun.AppName(push.Files.First(file => file.Path == "app.cs").Content) ?? Path.GetFileName(command.Target!)!, File.ReadAllText(FolderRun.DevTokenPath()).Trim(), push, token), cancellation.Token);
                case AppCommandKind.Pack:
                    var package = await new AppBuild().Pack(command.Target!, command.Output!, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine($"Packed {package.Descriptor.Id} {package.Descriptor.Version} -> {Path.GetFullPath(command.Output!)}");
                    return 0;
                case AppCommandKind.Install:
                case AppCommandKind.Run:
                    if (!File.Exists(command.Target)) { throw new ArgumentException("Specify an existing project or package file."); }
                    using (var http = AppCommands.Http(command.Endpoint))
                    {
                        var owner = new OwnerSessionClient(http);
                        await owner.Connect(command.Brain, cancellation.Token);
                        var commands = new AppCommands(owner, Console.In, Console.Out, !Console.IsInputRedirected && !Console.IsOutputRedirected);
                        if (command.Kind == AppCommandKind.Run) { await commands.Run(command.Target!, cancellation.Token); }
                        else { await using var archive = File.OpenRead(command.Target!); await commands.Install(archive, cancellation.Token); }
                    }
                    return 0;
                default: throw new ArgumentOutOfRangeException();
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception error) { LogFailure(error); return 1; }
        finally { Console.CancelKeyPress -= Handler; }
    }
}
