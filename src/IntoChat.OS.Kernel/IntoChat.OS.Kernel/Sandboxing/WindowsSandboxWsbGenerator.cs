using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using IntoChat.OS.Framework.Execution;

namespace IntoChat.OS.Kernel.Sandboxing;

/// <summary>
/// Generated Microsoft WinApp CLI v0.7.0 sparse-package AppContainer isolation configuration and invocation arguments.
/// </summary>
public sealed record WinAppPackageIsolationManifest(
    string CliVersion,
    string PackageIdentityName,
    string PublisherSubject,
    string WinAppConfigJson,
    string SparseAppxManifestXml,
    IReadOnlyList<string> CliArguments);

/// <summary>
/// Generates deterministic Windows Sandbox (<c>.wsb</c>) XML configurations and Microsoft WinApp CLI v0.7.0
/// AppContainer package isolation manifests for Tier 3 disposable Windows workloads without launching visible UI windows.
/// </summary>
public static class WindowsSandboxWsbGenerator
{
    public const string WinAppCliTargetVersion = "0.7.0";
    public const string SandboxReadOnlyInputFolder = @"C:\in";
    public const string SandboxScratchOutputFolder = @"C:\out";

    public static string BuildWsbXml(
        DisposableBatchSandboxSpec spec,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null)
    {
        ContainerBatchSandboxRunner.ValidateSpecCore(spec);

        var (roInput, scratchOut) = SandboxHostPathGuard.ValidateMountPair(
            spec.ReadOnlyInputHostPath,
            spec.ScratchOutputHostPath,
            userProfileOverride,
            intochatHomeOverride);

        var networking = spec.AllowPublicNetwork ? "Default" : "Disable";
        var logonCommand = BuildSandboxLogonCommand(spec.Command, spec.Arguments);

        var document = new XDocument(
            new XElement("Configuration",
                new XElement("VGpu", "Disable"),
                new XElement("Networking", networking),
                new XElement("AudioInput", "Disable"),
                new XElement("VideoInput", "Disable"),
                new XElement("ProtectedClient", "Enable"),
                new XElement("PrinterRedirection", "Disable"),
                new XElement("ClipboardRedirection", "Disable"),
                new XElement("MemoryInMB", spec.MemoryMb.ToString(CultureInfo.InvariantCulture)),
                new XElement("MappedFolders",
                    new XElement("MappedFolder",
                        new XElement("HostFolder", roInput),
                        new XElement("SandboxFolder", SandboxReadOnlyInputFolder),
                        new XElement("ReadOnly", "true")),
                    new XElement("MappedFolder",
                        new XElement("HostFolder", scratchOut),
                        new XElement("SandboxFolder", SandboxScratchOutputFolder),
                        new XElement("ReadOnly", "false"))),
                new XElement("LogonCommand",
                    new XElement("Command", logonCommand))));

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        };

        using (var writer = XmlWriter.Create(sb, settings))
        {
            document.Save(writer);
        }

        return sb.ToString() + "\n";
    }

    public static WinAppPackageIsolationManifest BuildWinAppPackageConfig(
        DisposableBatchSandboxSpec spec,
        string? userProfileOverride = null,
        string? intochatHomeOverride = null,
        string manifestFilePath = "AppxManifest.xml")
    {
        ContainerBatchSandboxRunner.ValidateSpecCore(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFilePath);

        var (roInput, scratchOut) = SandboxHostPathGuard.ValidateMountPair(
            spec.ReadOnlyInputHostPath,
            spec.ScratchOutputHostPath,
            userProfileOverride,
            intochatHomeOverride);

        var digestHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(spec.SandboxId.Trim())))[..12];
        var packageIdentityName = $"IntoChat.Sandbox.{digestHex}";
        const string publisherSubject = "CN=IntoChat Ephemeral Sandbox";

        var configJson = BuildWinAppJson(spec, packageIdentityName, publisherSubject, roInput, scratchOut);
        var appxManifestXml = BuildSparseAppxManifestXml(spec, packageIdentityName, publisherSubject);

        var cliArgs = new List<string>
        {
            "run",
            "--cli-version", WinAppCliTargetVersion,
            "--isolated",
            "--appcontainer",
            "--no-window",
            "--memory-mb", spec.MemoryMb.ToString(CultureInfo.InvariantCulture),
            "--network", spec.AllowPublicNetwork ? "internetClient" : "none",
            "--mount-ro", $"{roInput}={SandboxReadOnlyInputFolder}",
            "--mount-rw", $"{scratchOut}={SandboxScratchOutputFolder}",
            "--manifest", manifestFilePath,
            "--",
            spec.Command.Trim(),
        };

        foreach (var arg in spec.Arguments)
        {
            cliArgs.Add(arg ?? string.Empty);
        }

        return new WinAppPackageIsolationManifest(
            CliVersion: WinAppCliTargetVersion,
            PackageIdentityName: packageIdentityName,
            PublisherSubject: publisherSubject,
            WinAppConfigJson: configJson,
            SparseAppxManifestXml: appxManifestXml,
            CliArguments: cliArgs);
    }

    /// <summary>
    /// Enforces <c>SPIKE_WINDOWS=0</c> headless safety so automated tests or background daemon runs never spawn a visible
    /// <c>WindowsSandbox.exe</c> desktop window.
    /// </summary>
    public static void EnsureVisibleSandboxVmAllowed(Func<string, string?>? getEnv = null)
    {
        var readEnv = getEnv ?? Environment.GetEnvironmentVariable;
        if (string.Equals(readEnv("SPIKE_WINDOWS"), "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Launching visible WindowsSandbox.exe VMs is blocked while SPIKE_WINDOWS=0; use headless ContainerBatchSandboxRunner or WindowsAppContainerRunner.");
        }
    }

    private static string BuildSandboxLogonCommand(string command, IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsAppContainerRunner.FormatCommandLine(command.Trim(), arguments);
        }

        var parts = new List<string>(1 + arguments.Count) { QuoteCmdArg(command.Trim()) };
        foreach (var arg in arguments)
        {
            parts.Add(QuoteCmdArg(arg ?? string.Empty));
        }

        return string.Join(' ', parts);
    }

    private static string QuoteCmdArg(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string BuildWinAppJson(
        DisposableBatchSandboxSpec spec,
        string packageIdentityName,
        string publisherSubject,
        string roInput,
        string scratchOut)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("winappCliVersion", WinAppCliTargetVersion);
            writer.WriteString("packageIdentity", packageIdentityName);
            writer.WriteString("publisher", publisherSubject);
            writer.WriteString("trustLevel", "appContainer");
            writer.WriteString("runtimeBehavior", "appContainer");
            writer.WriteBoolean("allowFullTrust", false);
            writer.WriteBoolean("createNoWindow", true);
            writer.WriteString("network", spec.AllowPublicNetwork ? "internetClient" : "none");
            writer.WriteNumber("memoryLimitMb", spec.MemoryMb);
            writer.WriteNumber("cpuCount", spec.CpuCount);
            writer.WriteNumber("pidsLimit", spec.PidsLimit);

            writer.WriteStartArray("mounts");
            writer.WriteStartObject();
            writer.WriteString("hostPath", roInput);
            writer.WriteString("sandboxPath", SandboxReadOnlyInputFolder);
            writer.WriteBoolean("readOnly", true);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("hostPath", scratchOut);
            writer.WriteString("sandboxPath", SandboxScratchOutputFolder);
            writer.WriteBoolean("readOnly", false);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static string BuildSparseAppxManifestXml(
        DisposableBatchSandboxSpec spec,
        string packageIdentityName,
        string publisherSubject)
    {
        XNamespace appx = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";

        var capabilities = new XElement(appx + "Capabilities");
        if (spec.AllowPublicNetwork)
        {
            capabilities.Add(new XElement(appx + "Capability", new XAttribute("Name", "internetClient")));
        }

        var doc = new XDocument(
            new XElement(appx + "Package",
                new XAttribute(XNamespace.Xmlns + "uap10", uap10.NamespaceName),
                new XAttribute("IgnorableNamespaces", "uap10"),
                new XElement(appx + "Identity",
                    new XAttribute("Name", packageIdentityName),
                    new XAttribute("Publisher", publisherSubject),
                    new XAttribute("Version", "1.0.0.0")),
                new XElement(appx + "Properties",
                    new XElement(appx + "DisplayName", "IntoChat OS Ephemeral Sandbox"),
                    new XElement(appx + "PublisherDisplayName", "IntoChat OS"),
                    new XElement(uap10 + "AllowExternalContent", "true")),
                new XElement(appx + "Resources",
                    new XElement(appx + "Resource", new XAttribute("Language", "en-us"))),
                new XElement(appx + "Dependencies",
                    new XElement(appx + "TargetDeviceFamily",
                        new XAttribute("Name", "Windows.Desktop"),
                        new XAttribute("MinVersion", "10.0.19041.0"),
                        new XAttribute("MaxVersionTested", "10.0.26100.0"))),
                new XElement(appx + "Applications",
                    new XElement(appx + "Application",
                        new XAttribute("Id", "SandboxWorker"),
                        new XAttribute("Executable", Path.GetFileName(spec.Command.Trim())),
                        new XAttribute(uap10 + "TrustLevel", "appContainer"),
                        new XAttribute(uap10 + "RuntimeBehavior", "appContainer"))),
                capabilities));

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        }))
        {
            doc.Save(writer);
        }

        return sb.ToString() + "\n";
    }
}
