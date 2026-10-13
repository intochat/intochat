using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigitalBrain.Flutter.Aspire.Hosting;

internal static partial class LocalDesktopAccount
{
    internal const string ConfigurationKey = "DigitalBrain:Shell:LocalAccount";
    internal const string FileEnvironmentVariable = "DIGITALBRAIN_LOCAL_ACCOUNT_FILE";

    internal static bool Enabled(bool run, bool development, bool desktop, bool windows, string? principal)
        => run && development && desktop && windows && !string.IsNullOrWhiteSpace(principal);

    [SupportedOSPlatform("windows")]
    internal static string Ensure(string directory, string principalId)
    {
        if (!Username().IsMatch(principalId) || principalId.Length > 80 || principalId is "owner" or "integrations")
        { throw new ArgumentException("Local account must be a non-reserved lowercase username.", nameof(principalId)); }
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("No Windows user identity.");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        folder.Create(security);
        RefuseLink(folder);
        VerifyAccess(folder.GetAccessControl(), user, administrators);
        var file = new FileInfo(Path.Combine(folder.FullName, principalId + ".json"));
        if (!file.Exists)
        {
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { user, administrators })
            { fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow)); }
            var temporary = new FileInfo(Path.Combine(folder.FullName, Guid.NewGuid().ToString("N") + ".tmp"));
            try
            {
                using (var stream = temporary.Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, fileSecurity))
                {
                    JsonSerializer.Serialize(stream, new { principalId, password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) });
                    stream.Flush(true);
                }
                try { temporary.MoveTo(file.FullName, overwrite: false); }
                catch (IOException) when (File.Exists(file.FullName)) { /* Another launcher won the atomic create. */ }
            }
            finally { if (File.Exists(temporary.FullName) && temporary.FullName != file.FullName) { temporary.Delete(); } }
        }
        file.Refresh();
        RefuseLink(file);
        VerifyAccess(file.GetAccessControl(), user, administrators);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file.FullName));
            var root = json.RootElement;
            if (root.GetProperty("principalId").GetString() != principalId || root.GetProperty("password").GetString() is not { Length: 64 } password
                || !password.All(char.IsAsciiHexDigit))
            { throw new InvalidOperationException("Local account file is invalid; it has not been replaced."); }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidOperationException("Local account file is invalid; it has not been replaced."); }
        return file.FullName;
    }

    private static void RefuseLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidOperationException("Local account storage cannot be a filesystem link."); }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyAccess(FileSystemSecurity security, SecurityIdentifier user, SecurityIdentifier administrators)
    {
        if (!security.AreAccessRulesProtected || security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference != user && rule.IdentityReference != administrators))
        { throw new InvalidOperationException("Local account storage must be private to this Windows user and administrators."); }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Username();
}
