using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntoChat.Sdk;

namespace IntoChat.OS.Modules.Apps;

/// <summary>Declarative slot capability requirement extracted from <see cref="SlotAttribute"/>.</summary>
[GenerateSerializer, Alias("apps.slot-spec")]
public sealed record AppSlotSpec(
    [property: Id(0)] string Name,
    [property: Id(1)] string Contract,
    [property: Id(2)] bool Required = true,
    [property: Id(3)] string Description = "",
    [property: Id(4)] FolderAccess Access = FolderAccess.Read,
    [property: Id(5)] string DefaultPath = "");

/// <summary>Declarative command exposed by an <see cref="AppPackage"/>.</summary>
[GenerateSerializer, Alias("apps.command-spec")]
public sealed record AppCommandSpec(
    [property: Id(0)] string Name,
    [property: Id(1)] string Description = "");

/// <summary>Typed capabilities, signals, schedules, and commands required by a compiled <see cref="AppPackage"/>.</summary>
[GenerateSerializer, Alias("apps.package-needs")]
public sealed record AppPackageNeeds(
    [property: Id(0)] AppSlotSpec[] Slots,
    [property: Id(1)] string[] Signals,
    [property: Id(2)] string[] Schedules,
    [property: Id(3)] AppCommandSpec[] Commands)
{
    public static AppPackageNeeds Empty { get; } = new([], [], [], []);
}

/// <summary>Single C# source file belonging to an <see cref="AppPackage"/>.</summary>
[GenerateSerializer, Alias("apps.package-source-file")]
public sealed record AppSourceFile(
    [property: Id(0)] string Path,
    [property: Id(1)] string Code);

/// <summary>Portable, shareable C# app artifact compiled once and executed inside <c>IntoChat.OS.Sandbox</c>.</summary>
[GenerateSerializer, Alias("apps.package")]
public sealed record AppPackage(
    [property: Id(0)] string Key,
    [property: Id(1)] string Version,
    [property: Id(2)] string Title,
    [property: Id(3)] string Summary,
    [property: Id(4)] string Icon,
    [property: Id(5)] string Category,
    [property: Id(6)] string Author,
    [property: Id(7)] AppSourceFile[] SourceFiles,
    [property: Id(8)] AppPackageNeeds Needs,
    [property: Id(9)] string DefaultSettingsJson,
    [property: Id(10)] string Digest)
{
    public static string ComputeDigest(string key, string version, IReadOnlyList<AppSourceFile> sources, string defaultSettingsJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(sources);

        var canonical = JsonSerializer.Serialize(new
        {
            key,
            version,
            defaultSettingsJson,
            sources = sources
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new { file.Path, file.Code }),
        });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>Explicitly approved binding of an app's folder slot to a concrete host folder on Windows.</summary>
[GenerateSerializer, Alias("apps.folder-binding")]
public sealed record FolderSlotBinding(
    [property: Id(0)] string Slot,
    [property: Id(1)] string HostPath,
    [property: Id(2)] FolderAccess Access = FolderAccess.Read,
    [property: Id(3)] bool AutoSuggested = false);

/// <summary>Structured prompt returned when an app needs a connector (such as Windows) that is not yet enabled.</summary>
[GenerateSerializer, Alias("apps.missing-connector-proposal")]
public sealed record MissingConnectorProposal(
    [property: Id(0)] string ConnectorId,
    [property: Id(1)] string Title,
    [property: Id(2)] string Reason,
    [property: Id(3)] string SlotName,
    [property: Id(4)] bool CanAutoConnectLocal);
