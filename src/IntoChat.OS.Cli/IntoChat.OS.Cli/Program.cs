namespace IntoChat.OS.Cli;

internal static class Program
{
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
                    Console.WriteLine("intochat pack <project.csproj> -o <app.intochat-app>\nintochat run <project> [--endpoint <url>] [--brain <id>] (not implemented)\nintochat install <package> [--endpoint <url>] [--brain <id>] (not implemented)");
                    return 0;
                case AppCommandKind.Pack:
                    var package = await new AppBuild().Pack(command.Target!, command.Output!, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine($"Packed {package.Descriptor.Id} {package.Descriptor.Version} -> {Path.GetFullPath(command.Output!)}");
                    return 0;
                default: throw new NotSupportedException($"{command.Kind} is not implemented yet.");
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { Console.CancelKeyPress -= Handler; }
    }
}
