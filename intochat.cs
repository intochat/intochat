#!/usr/bin/env -S dotnet --
#:project src/IntoChat.OS.LocalSetup/IntoChat.OS.LocalSetup/IntoChat.OS.LocalSetup.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// Starts IntoChat.OS: `dotnet run intochat.cs [default|headless|local|preflight|reset|<config>.json] [-- <AppHost arguments>]` from the repository root.
// Profiles: default runs the Flutter shell; headless leaves it out; local runs preflight, pulls the model if it is missing and
// starts the lean host-Ollama profile; preflight only checks the machine; reset forwards to the AppHost; a .json file is an extra
// configuration file the AppHost loads. Argument parsing, preflight and the AppHost switches live in IntoChat.OS.LocalSetup.
using System.Diagnostics;
using IntoChat.OS.LocalSetup;

var command = LauncherCommand.Parse(args);
if (command.ImmediateExitCode is { } immediate) { Console.Error.WriteLine(command.Error); return immediate; }
if (command.Kind == LauncherKind.Preflight) { return await Preflight.RunAsync(args, Console.Out, CancellationToken.None); }

var root = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var appHost = Path.Combine(root, "src", "IntoChat.OS.AppHost", "IntoChat.OS.AppHost.csproj");
IReadOnlyList<string> appHostArguments;
switch (command.Kind)
{
    case LauncherKind.Local:
        var launch = await Preflight.PrepareLocalAsync(command, Console.Out, CancellationToken.None);
        if (launch.ExitCode != 0) { return launch.ExitCode; }
        appHostArguments = launch.Arguments;
        break;
    case LauncherKind.Reset:
        appHostArguments = ["reset", .. command.Remaining];
        break;
    case LauncherKind.ConfigFile when !File.Exists(Path.GetFullPath(command.ProfileWord!)):
        Console.Error.WriteLine($"Config file '{Path.GetFullPath(command.ProfileWord!)}' does not exist.");
        return 2;
    default:
        appHostArguments = command.AppHostArguments();
        break;
}

var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(appHost)!, UseShellExecute = false };
start.Environment["CodeGraphRefresh"] = "false";
foreach (var argument in (string[])["run", "--project", appHost, "--"]) { start.ArgumentList.Add(argument); }
foreach (var argument in appHostArguments) { start.ArgumentList.Add(argument); }

using var appHostProcess = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {appHost}.");
Console.CancelKeyPress += (_, press) => press.Cancel = true;
await appHostProcess.WaitForExitAsync();
return appHostProcess.ExitCode;
