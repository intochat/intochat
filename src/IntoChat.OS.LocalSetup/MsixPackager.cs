using System.Diagnostics;
using System.Security;
using System.Text;

namespace IntoChat.OS.LocalSetup;

public static class MsixPackager
{
    public const string DefaultName = "IntoChat.OS";
    public const string DefaultPublisher = "CN=IntoChat Dev";
    public const string DefaultPublisherDisplayName = "IntoChat";
    public const string DefaultBaseUri = "https://github.com/intochat/intochat/releases/latest/download";

    public static async Task<int> RunAsync(string[] args)
    {
        var build = 0;
        var name = DefaultName;
        var publisher = DefaultPublisher;
        var publisherDisplayName = DefaultPublisherDisplayName;
        var baseUri = DefaultBaseUri;
        var signing = "None";
        string? certPath = null;
        string? certPass = null;
        string? certThumbprint = null;
        var timestampUrl = "http://timestamp.digicert.com";
        string? trustedSigningEndpoint = null;
        string? trustedSigningAccount = null;
        string? trustedSigningProfile = null;
        string? trustedSigningDlib = null;
        var skipOs = false;
        var skipShell = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--build" && i + 1 < args.Length)
            {
                build = int.Parse(args[++i]);
            }
            else if (arg == "--name" && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else if (arg == "--publisher" && i + 1 < args.Length)
            {
                publisher = args[++i];
            }
            else if (arg == "--publisher-display-name" && i + 1 < args.Length)
            {
                publisherDisplayName = args[++i];
            }
            else if (arg == "--base-uri" && i + 1 < args.Length)
            {
                baseUri = args[++i];
            }
            else if (arg == "--signing" && i + 1 < args.Length)
            {
                signing = args[++i];
            }
            else if (arg == "--certificate-path" && i + 1 < args.Length)
            {
                certPath = args[++i];
                signing = "Certificate";
            }
            else if (arg == "--certificate-password" && i + 1 < args.Length)
            {
                certPass = args[++i];
            }
            else if (arg == "--certificate-thumbprint" && i + 1 < args.Length)
            {
                certThumbprint = args[++i];
                signing = "Certificate";
            }
            else if (arg == "--timestamp-url" && i + 1 < args.Length)
            {
                timestampUrl = args[++i];
            }
            else if (arg == "--trusted-signing-endpoint" && i + 1 < args.Length)
            {
                trustedSigningEndpoint = args[++i];
            }
            else if (arg == "--trusted-signing-account" && i + 1 < args.Length)
            {
                trustedSigningAccount = args[++i];
            }
            else if (arg == "--trusted-signing-profile" && i + 1 < args.Length)
            {
                trustedSigningProfile = args[++i];
            }
            else if (arg == "--trusted-signing-dlib" && i + 1 < args.Length)
            {
                trustedSigningDlib = args[++i];
            }
            else if (arg == "--skip-os")
            {
                skipOs = true;
            }
            else if (arg == "--skip-shell")
            {
                skipShell = true;
            }
        }

        var repoRoot = FindRepoRoot();
        var versionFile = Path.Combine(repoRoot, "VERSION");
        var semver = (await File.ReadAllTextAsync(versionFile)).Trim();
        var match = System.Text.RegularExpressions.Regex.Match(semver, @"^(\d+)\.(\d+)\.(\d+)");
        if (!match.Success)
        {
            throw new InvalidOperationException($"VERSION '{semver}' is not major.minor.patch.");
        }

        var version = $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}.{build}";
        var packageFile = $"{name}_{version}_x64.msix";

        var outDir = Path.Combine(repoRoot, "artifacts", "msix");
        var layoutDir = Path.Combine(outDir, "layout");
        Directory.CreateDirectory(layoutDir);

        if (!skipOs)
        {
            var osTarget = Path.Combine(layoutDir, "os");
            if (Directory.Exists(osTarget))
            {
                Directory.Delete(osTarget, true);
            }
            var kernelProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel.csproj");
            RunChecked("dotnet", ["publish", kernelProj, "-c", "Release", "-r", "win-x64", "/p:IntoChatOSAot=true", "--self-contained", "true", "-o", osTarget, "--nologo"]);
        }

        if (!skipShell)
        {
            var shellDir = Path.Combine(repoRoot, "src", "IntoChat.OS.Modules", "Google", "Flutter", "app", "shell");
            RunChecked("flutter", ["build", "windows", "--release", $"--build-name={semver}", $"--build-number={build}"], workingDirectory: shellDir);
            var shellTarget = Path.Combine(layoutDir, "shell");
            if (Directory.Exists(shellTarget))
            {
                Directory.Delete(shellTarget, true);
            }
            CopyDirectory(Path.Combine(shellDir, "build", "windows", "x64", "runner", "Release"), shellTarget);
        }

        var launcherDir = Path.Combine(outDir, "launcher");
        var desktopProj = Path.Combine(repoRoot, "src", "IntoChat.OS.Desktop", "IntoChat.OS.Desktop.csproj");
        RunChecked("dotnet", ["publish", desktopProj, "-c", "Release", "-r", "win-x64", "-o", launcherDir, "--nologo"]);
        File.Copy(Path.Combine(launcherDir, "IntoChat.OS.Desktop.exe"), Path.Combine(layoutDir, "IntoChat.OS.Desktop.exe"), overwrite: true);

        // Assets
        var assetsDir = Path.Combine(layoutDir, "Assets");
        Directory.CreateDirectory(assetsDir);
        var iconsDir = Path.Combine(repoRoot, "src", "IntoChat.OS.Modules", "Google", "Flutter", "app", "shell", "web", "icons");
        var icon192 = Path.Combine(iconsDir, "Icon-192.png");
        if (File.Exists(icon192))
        {
            File.Copy(icon192, Path.Combine(assetsDir, "Square44x44Logo.png"), overwrite: true);
            File.Copy(icon192, Path.Combine(assetsDir, "Square150x150Logo.png"), overwrite: true);
            File.Copy(icon192, Path.Combine(assetsDir, "StoreLogo.png"), overwrite: true);
        }

        // Validate package layout
        foreach (var required in new[] { "os/IntoChat.OS.Kernel.exe", "os/features", "shell/intochat_os_flutter.exe", "IntoChat.OS.Desktop.exe" })
        {
            var full = Path.Combine(layoutDir, required.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                Console.WriteLine($"Notice: {required} not present in layout yet (might be partial or dry run).");
            }
        }

        // Write AppxManifest.xml
        var manifestTemplate = AppxManifestTemplate;
        manifestTemplate = ExpandTemplate(manifestTemplate, name, publisher, publisherDisplayName, version, baseUri, packageFile);
        await File.WriteAllTextAsync(Path.Combine(layoutDir, "AppxManifest.xml"), manifestTemplate, new UTF8Encoding(false));

        var msixPath = Path.Combine(outDir, packageFile);
        var makeappx = FindSdkTool("makeappx.exe");
        if (makeappx != null)
        {
            RunChecked(makeappx, ["pack", "/d", layoutDir, "/p", msixPath, "/o"]);
        }
        else
        {
            Console.WriteLine("Warning: makeappx.exe not found in Windows SDK path. Skipping packaging step.");
        }

        // Write appinstaller
        var appinstallerTemplate = AppinstallerTemplate;
        appinstallerTemplate = ExpandTemplate(appinstallerTemplate, name, publisher, publisherDisplayName, version, baseUri, packageFile);
        await File.WriteAllTextAsync(Path.Combine(outDir, $"{name}.appinstaller"), appinstallerTemplate, new UTF8Encoding(false));

        Console.WriteLine($"Package configured at {msixPath} (version {version}).");
        return 0;
    }

    private static string ExpandTemplate(string template, string name, string publisher, string publisherDisplayName, string version, string baseUri, string packageFile)
    {
        return template
            .Replace("$name$", SecurityElement.Escape(name))
            .Replace("$publisher$", SecurityElement.Escape(publisher))
            .Replace("$publisherDisplayName$", SecurityElement.Escape(publisherDisplayName))
            .Replace("$version$", SecurityElement.Escape(version))
            .Replace("$baseUri$", SecurityElement.Escape(baseUri.TrimEnd('/')))
            .Replace("$package$", SecurityElement.Escape(packageFile));
    }

    private static string? FindSdkTool(string tool)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var kits = Path.Combine(programFiles, "Windows Kits", "10", "bin");
        if (!Directory.Exists(kits))
        {
            return null;
        }

        var dirs = Directory.GetDirectories(kits, "10.*")
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, "x64", tool);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
            {
                return dir.FullName;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private static void RunChecked(string file, string[] args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (workingDirectory != null)
        {
            psi.WorkingDirectory = workingDirectory;
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} exited with code {proc.ExitCode}.");
        }
    }

    private const string AppxManifestTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package
          xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
          xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
          xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
          xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
          xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
          IgnorableNamespaces="uap uap3 uap5 rescap">
          <Identity Name="$name$" Publisher="$publisher$" Version="$version$" ProcessorArchitecture="x64" />
          <Properties>
            <DisplayName>IntoChat OS</DisplayName>
            <PublisherDisplayName>$publisherDisplayName$</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <Description>The IntoChat operating system, its Windows shell and the Windows device connector.</Description>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Resources>
            <Resource Language="en-us" />
          </Resources>
          <Applications>
            <Application Id="IntoChatOS" Executable="IntoChat.OS.Desktop.exe" EntryPoint="Windows.FullTrustApplication">
              <uap:VisualElements DisplayName="IntoChat OS" Description="IntoChat OS" BackgroundColor="transparent"
                                  Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
              <Extensions>
                <uap3:Extension Category="windows.appExecutionAlias" Executable="IntoChat.OS.Desktop.exe" EntryPoint="Windows.FullTrustApplication">
                  <uap3:AppExecutionAlias>
                    <desktop:ExecutionAlias xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10" Alias="intochat-os.exe" />
                  </uap3:AppExecutionAlias>
                </uap3:Extension>
              </Extensions>
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    private const string AppinstallerTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018" Version="$version$" Uri="$baseUri$/IntoChat.OS.appinstaller">
          <MainPackage Name="$name$" Publisher="$publisher$" Version="$version$" ProcessorArchitecture="x64" Uri="$baseUri$/$package$" />
          <UpdateSettings>
            <OnLaunch HoursBetweenUpdateChecks="24" />
            <AutomaticBackgroundTask />
          </UpdateSettings>
        </AppInstaller>
        """;
}
