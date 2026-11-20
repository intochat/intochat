namespace IntoChat.OS.LocalSetup;

public enum LauncherKind { Default, Headless, ConfigFile, Local, Preflight, Reset, Unknown }

// What intochat-os.cs was asked to do. The first argument not starting with '-' selects the profile; the
// launcher's own flags are consumed, everything else (and everything after a bare "--") goes to the AppHost.
public sealed record LauncherCommand(
    LauncherKind Kind,
    string? ProfileWord,
    IReadOnlyList<string> Remaining,
    bool SkipPreflight = false,
    bool Json = false,
    string? Fake = null,
    bool Pull = false,
    bool NoFail = false,
    string? Model = null,
    string? Endpoint = null,
    bool AllowRegistry = false,
    string? Error = null)
{
    public const int UsageExitCode = 2;

    // Exit code to stop with before doing anything (unknown profile, bad flag); null when the command can run.
    public int? ImmediateExitCode => Kind == LauncherKind.Unknown ? UsageExitCode : null;

    public LocalModelOptions ModelOptions => new()
    {
        Model = Model ?? LocalModelOptions.DefaultModel,
        Endpoint = Endpoint ?? LocalModelOptions.DefaultEndpoint,
        EndpointExplicit = Endpoint is not null,
        AllowRegistry = AllowRegistry,
    };

    public static LauncherCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var kind = LauncherKind.Default;
        string? word = null;
        var index = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            word = args[0];
            index = 1;
            kind = word switch
            {
                "default" => LauncherKind.Default,
                "headless" => LauncherKind.Headless,
                "local" => LauncherKind.Local,
                "preflight" => LauncherKind.Preflight,
                "reset" => LauncherKind.Reset,
                _ when word.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => LauncherKind.ConfigFile,
                _ => LauncherKind.Unknown,
            };
        }

        var command = new LauncherCommand(kind, word, []);
        if (kind == LauncherKind.Unknown)
        {
            return command with { Error = $"Unknown profile '{word}'. Use default, headless, local, preflight, reset or a .json config file." };
        }

        var remaining = new List<string>();
        for (; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--")
            {
                remaining.AddRange(args.Skip(index + 1));
                break;
            }
            switch (argument)
            {
                case "--skip-preflight": command = command with { SkipPreflight = true }; break;
                case "--json": command = command with { Json = true }; break;
                case "--pull": command = command with { Pull = true }; break;
                case "--no-fail": command = command with { NoFail = true }; break;
                case "--allow-registry": command = command with { AllowRegistry = true }; break;
                case "--fake" or "--model" or "--endpoint":
                    if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
                    {
                        return command with { Kind = LauncherKind.Unknown, Error = $"{argument} needs a value." };
                    }
                    var value = args[++index];
                    command = argument switch
                    {
                        "--fake" => command with { Fake = value },
                        "--model" => command with { Model = value },
                        _ => command with { Endpoint = value },
                    };
                    break;
                default: remaining.Add(argument); break;
            }
        }
        return command with { Remaining = remaining };
    }

    // AppHost arguments for default, headless and <config>.json, the unchanged pre-existing semantics.
    public IReadOnlyList<string> ProfileArguments() => Kind switch
    {
        LauncherKind.Headless => [LocalSetupKeys.Argument(LocalSetupKeys.ShellEnabled, "false")],
        LauncherKind.ConfigFile => [LocalSetupKeys.Argument(LocalSetupKeys.ConfigFile, Path.GetFullPath(ProfileWord!))],
        _ => [],
    };

    // AppHost arguments for the local profile: the local topology (host Ollama), the shell only when the Flutter SDK is
    // available, and the model override when one was requested.
    public IReadOnlyList<string> LocalAppHostArguments(bool shellAvailable)
    {
        var arguments = new List<string>
        {
            LocalSetupKeys.Argument(LocalSetupKeys.Topology, LocalSetupKeys.LocalTopology),
        };
        if (!shellAvailable) { arguments.Add(LocalSetupKeys.Argument(LocalSetupKeys.ShellEnabled, false.ToString().ToLowerInvariant())); }
        if (Model is not null) { arguments.Add(LocalSetupKeys.Argument(LocalSetupKeys.ModelOverride, Model)); }
        arguments.AddRange(Remaining);
        return arguments;
    }

    public IReadOnlyList<string> AppHostArguments() => [.. ProfileArguments(), .. Remaining];
}
