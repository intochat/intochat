#!/usr/bin/env -S dotnet --
// Starts DigitalBrain: `dotnet run digitalbrain.cs [-- <AppHost arguments>]` from the repository root.
// The AppHost stays the composition root (src/DigitalBrain.AppHost); this file launches it with
// `dotnet run --project` and forwards arguments, exit code and Ctrl+C. An Aspire AppHost needs its own
// SDK and project, so it is launched rather than referenced. The OS it starts loads ./features at boot.
using System.Diagnostics;

var root = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var appHost = Path.Combine(root, "src", "DigitalBrain.AppHost", "DigitalBrain.AppHost.csproj");
var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(appHost)!, UseShellExecute = false };
foreach (var argument in (string[])["run", "--project", appHost, "--"]) { start.ArgumentList.Add(argument); }
foreach (var argument in args) { start.ArgumentList.Add(argument); }

using var appHostProcess = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {appHost}.");
// The console delivers Ctrl+C to the AppHost too; this process stays up until the AppHost has shut down.
Console.CancelKeyPress += (_, press) => press.Cancel = true;
await appHostProcess.WaitForExitAsync();
return appHostProcess.ExitCode;
