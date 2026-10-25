using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework;

// Where local state lives: IntoChatOS:Home, or ~/.intochat when unset. The brain log lives in <Home>/state and
// secrets in <Home>/secrets (owner-only). Os-local until Phase 3 moves Home into DigitalBrain.Runtime.Orleans.
public sealed record StateHome
{
    public const string DefaultDirectoryName = ".intochat";
    public const string StateDirectoryName = "state";
    public const string SecretsDirectoryName = "secrets";

    private StateHome(string path) => Path = path;

    public string Path { get; }
    public string State => System.IO.Path.Combine(Path, StateDirectoryName);
    public string Secrets => System.IO.Path.Combine(Path, SecretsDirectoryName);

    public static StateHome Resolve(IConfiguration? configuration)
    {
        var configured = configuration?[IntoChatOSNames.HomeKey];
        if (string.IsNullOrWhiteSpace(configured)) { return Default(); }
        if (!System.IO.Path.IsPathFullyQualified(configured))
        { throw new InvalidOperationException($"{IntoChatOSNames.HomeKey} must be an absolute path."); }
        return new StateHome(System.IO.Path.GetFullPath(configured));
    }

    public static StateHome Default()
        => new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultDirectoryName));

    // Creates <Home>/secrets readable only by the current user: 0700 on Unix, a protected current-user ACL on Windows.
    public string CreateSecrets()
    {
        var secrets = Secrets;
        if (OperatingSystem.IsWindows())
        {
            var info = Directory.CreateDirectory(secrets);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            Directory.CreateDirectory(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return secrets;
    }
}
