namespace IntoChat.OS.Cli;

internal enum AppCommandKind { Help, Run, Pack, Install }

internal sealed record AppCommand(AppCommandKind Kind, string? Target = null, string? Output = null, Uri? Endpoint = null, string? Brain = null)
{
    internal static AppCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args is ["help" or "--help" or "-h"]) { return new(AppCommandKind.Help); }
        var kind = args[0] switch
        {
            "run" => AppCommandKind.Run,
            "pack" => AppCommandKind.Pack,
            "install" => AppCommandKind.Install,
            _ => throw new ArgumentException("Use run <project>, pack <project> -o <package>, or install <package>.", nameof(args)),
        };
        string? target = null;
        string? output = null;
        string? brain = null;
        Uri? endpoint = null;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith('-'))
            {
                if (target is not null || string.IsNullOrWhiteSpace(argument))
                { throw new ArgumentException("Specify exactly one project or package path.", nameof(args)); }
                target = argument;
                continue;
            }
            if (argument is not ("-o" or "--endpoint" or "--brain"))
            { throw new ArgumentException($"Unknown option '{argument}'.", nameof(args)); }
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith('-'))
            { throw new ArgumentException($"{argument} needs a value.", nameof(args)); }
            var value = args[index];
            switch (argument)
            {
                case "-o":
                    if (kind != AppCommandKind.Pack || output is not null)
                    { throw new ArgumentException("Use -o once, with pack only.", nameof(args)); }
                    output = value;
                    break;
                case "--brain":
                    if (kind == AppCommandKind.Pack || brain is not null)
                    { throw new ArgumentException("Use --brain once, with run or install.", nameof(args)); }
                    brain = value;
                    break;
                case "--endpoint":
                    if (kind == AppCommandKind.Pack || endpoint is not null)
                    { throw new ArgumentException("Use --endpoint once, with run or install.", nameof(args)); }
                    if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint)
                        || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0)
                    { throw new ArgumentException("The endpoint must be an HTTP(S) URL without embedded credentials.", nameof(args)); }
                    break;
            }
        }
        if (target is null) { throw new ArgumentException("Specify a project or package path.", nameof(args)); }
        if (kind == AppCommandKind.Pack && output is null)
        { throw new ArgumentException("pack requires -o <package>.", nameof(args)); }
        return new(kind, target, output, endpoint, brain);
    }
}
