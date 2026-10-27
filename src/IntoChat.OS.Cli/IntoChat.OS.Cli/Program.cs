namespace IntoChat.OS.Cli;

internal static class Program
{
    private static void LogFailure(Exception error) => Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        void Handler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancellation.Cancel(); }
        Console.CancelKeyPress += Handler;
        try
        {
            if (args is ["__extract-app-metadata", var assembly, var destination])
            { await BuildMetadata.Extract(assembly, destination, cancellation.Token).ConfigureAwait(false); return 0; }
            var command = AppCommand.Parse(args);
            switch (command.Kind)
            {
                case AppCommandKind.Help:
                    Console.WriteLine("intochat pack <project.csproj> -o <app.intochat-app>\nintochat run <project> [--endpoint <url>] [--brain <id>]\nintochat install <package> [--endpoint <url>] [--brain <id>]");
                    return 0;
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
