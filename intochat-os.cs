#!/usr/bin/env -S dotnet --
// Starts IntoChat.OS: `dotnet run intochat-os.cs [default|headless|<config>.json] [-- <AppHost arguments>]` from the repository root.
// Profiles: default runs the Flutter shell; headless leaves it out; a .json file is an extra configuration file the AppHost loads.
// The AppHost stays the composition root (src/IntoChat.OS.AppHost); this file launches it with
// `dotnet run --project` and forwards arguments, exit code and Ctrl+C. An Aspire AppHost needs its own
// SDK and project, so it is launched rather than referenced. The OS it starts loads ./features at boot.
using System.Diagnostics;

var root = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var appHost = Path.Combine(root, "src", "IntoChat.OS.AppHost", "IntoChat.OS.AppHost.csproj");
var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(appHost)!, UseShellExecute = false };
foreach (var argument in (string[])["run", "--project", appHost, "--"]) { start.ArgumentList.Add(argument); }
var forwarded = args.ToList();
var profileArguments = new List<string>();
if (forwarded.Count > 0 && !forwarded[0].StartsWith('-'))
{
    var profile = forwarded[0];
    forwarded.RemoveAt(0);
    if (profile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        var path = Path.GetFullPath(profile);
        if (!File.Exists(path)) { Console.Error.WriteLine($"Config file '{path}' does not exist."); return 2; }
        profileArguments.Add($"--IntoChatOS:ConfigFile={path}");
    }
    else if (profile == "headless") { profileArguments.Add("--IntoChatOS:Shell:Enabled=false"); }
    else if (profile != "default") { Console.Error.WriteLine($"Unknown profile '{profile}'. Use default, headless or a .json config file."); return 2; }
}
if (forwarded.FirstOrDefault() == "--") { forwarded.RemoveAt(0); }
foreach (var argument in profileArguments.Concat(forwarded)) { start.ArgumentList.Add(argument); }

using var appHostProcess = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {appHost}.");
// The console delivers Ctrl+C to the AppHost too; this process stays up until the AppHost has shut down.
Console.CancelKeyPress += (_, press) => press.Cancel = true;
await appHostProcess.WaitForExitAsync();
return appHostProcess.ExitCode;
