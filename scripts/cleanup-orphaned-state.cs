#!/usr/bin/env -S dotnet --
// Dry-run cleanup and audit tool for orphaned files and dead brains in ~/.intochat
// Usage:
//   dotnet run scripts/cleanup-orphaned-state.cs           (dry-run report only)
//   dotnet run scripts/cleanup-orphaned-state.cs --confirm (safely quarantines orphaned items)

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var home = Environment.GetEnvironmentVariable("INTOCHAT_HOME") 
    ?? Path.Combine(userProfile, ".intochat");

var stateDir = Path.Combine(home, "state");
Console.WriteLine($"=== IntoChat State Orphan Audit ===");
Console.WriteLine($"Target directory: {stateDir}");

if (!Directory.Exists(stateDir))
{
    Console.WriteLine($"State directory does not exist: {stateDir}");
    return;
}

var isDryRun = !args.Contains("--confirm");
if (isDryRun)
{
    Console.WriteLine("MODE: DRY RUN (inspection only, no changes will be made)");
}
else
{
    Console.WriteLine("MODE: CONFIRMED QUARANTINE (moving orphaned items to safe backup folder)");
}
Console.WriteLine();

// 1. Audit PubSubStore.bak
var pubSubBakDir = Path.Combine(stateDir, "files", "PubSubStore.bak");
var pubSubBakFiles = Directory.Exists(pubSubBakDir) 
    ? Directory.GetFiles(pubSubBakDir, "*.json").ToList() 
    : new List<string>();

long pubSubBakBytes = 0;
Console.WriteLine($"[1] PubSubStore Backups ({pubSubBakDir}):");
if (pubSubBakFiles.Count == 0)
{
    Console.WriteLine("    No backup files found.");
}
else
{
    Console.WriteLine($"    Found {pubSubBakFiles.Count} orphaned backup files:");
    foreach (var file in pubSubBakFiles)
    {
        var info = new FileInfo(file);
        pubSubBakBytes += info.Length;
        Console.WriteLine($"      - {info.Name} ({info.Length} bytes, LastModified: {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC)");
    }
    Console.WriteLine($"    Total PubSubStore.bak size: {pubSubBakBytes:N0} bytes ({pubSubBakBytes / 1024.0:F2} KB)");
}
Console.WriteLine();

// 2. Audit Dead / Abandoned Brains in state/
var deadBrains = new List<(string BrainId, string Path, long TotalBytes, List<string> Files)>();

foreach (var dir in Directory.GetDirectories(stateDir))
{
    var dirName = Path.GetFileName(dir);
    // Brain directories are 64-character SHA-256 hex strings
    if (dirName.Length != 64 || !dirName.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
    {
        continue;
    }

    var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToList();
    long totalBytes = files.Sum(f => new FileInfo(f).Length);

    // A brain is dead/empty if it only has brain.key (60 bytes) and 0-byte initial.log / writer.lock
    bool hasMeaningfulData = files.Any(f =>
    {
        var name = Path.GetFileName(f);
        var len = new FileInfo(f).Length;
        return len > 0 && !string.Equals(name, "brain.key", StringComparison.OrdinalIgnoreCase);
    });

    if (!hasMeaningfulData)
    {
        deadBrains.Add((dirName, dir, totalBytes, files.Select(f => Path.GetFileName(f) ?? string.Empty).ToList()));
    }
}

Console.WriteLine($"[2] Dead / Abandoned Brain Directories ({stateDir}):");
if (deadBrains.Count == 0)
{
    Console.WriteLine("    No abandoned brain directories found.");
}
else
{
    Console.WriteLine($"    Found {deadBrains.Count} abandoned brain directories (no .log or .snapshot data):");
    foreach (var dead in deadBrains)
    {
        Console.WriteLine($"      - Brain [{dead.BrainId}] ({dead.TotalBytes} bytes, files: {string.Join(", ", dead.Files)})");
    }
}
Console.WriteLine();

// Summary & Action
Console.WriteLine("=== Audit Summary ===");
Console.WriteLine($"Orphaned PubSubStore.bak files: {pubSubBakFiles.Count} ({pubSubBakBytes:N0} bytes)");
Console.WriteLine($"Abandoned brain directories:    {deadBrains.Count} ({deadBrains.Sum(d => d.TotalBytes):N0} bytes)");

if (isDryRun)
{
    Console.WriteLine();
    Console.WriteLine("[DRY RUN COMPLETE] No files were touched or deleted.");
    Console.WriteLine("To quarantine these orphaned items, run: dotnet run scripts/cleanup-orphaned-state.cs --confirm");
    return;
}

// Perform confirmed quarantine
var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var quarantineRoot = Path.Combine(home, "quarantine", timestamp);
Directory.CreateDirectory(quarantineRoot);
Console.WriteLine();
Console.WriteLine($"Moving items to quarantine: {quarantineRoot}...");

if (pubSubBakFiles.Count > 0 && Directory.Exists(pubSubBakDir))
{
    var targetPubSub = Path.Combine(quarantineRoot, "PubSubStore.bak");
    Directory.Move(pubSubBakDir, targetPubSub);
    Console.WriteLine($"  Moved {pubSubBakDir} -> {targetPubSub}");
}

foreach (var dead in deadBrains)
{
    var targetBrain = Path.Combine(quarantineRoot, "brains", dead.BrainId);
    Directory.CreateDirectory(Path.GetDirectoryName(targetBrain)!);
    Directory.Move(dead.Path, targetBrain);
    Console.WriteLine($"  Moved brain [{dead.BrainId}] -> {targetBrain}");
}

Console.WriteLine();
Console.WriteLine("[QUARANTINE COMPLETE] All orphaned items have been safely moved.");
Console.WriteLine($"Quarantine location: {quarantineRoot}");
