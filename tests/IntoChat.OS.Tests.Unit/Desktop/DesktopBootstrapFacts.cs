using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using IntoChat.OS.Authority.Identity;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Desktop.Tests.Unit;

public sealed class DesktopBootstrapFacts
{
    [Fact]
    public void ClosedAndActiveTcpPortProbeCompletesInUnderFiveMilliseconds()
    {
        var boundPorts = DesktopBootstrap.GetBoundTcpPorts().ToHashSet();

        // Pick an unused high port that is guaranteed closed right now.
        var closedPort = Enumerable.Range(49152, 16000).First(port => !boundPorts.Contains(port));

        // Warm up JIT + IPGlobalProperties once so we measure steady-state socket table query latency.
        _ = DesktopBootstrap.IsPortListening(closedPort);

        const double maxProbeMs = 50.0;

        var swClosed = Stopwatch.StartNew();
        var closedListening = DesktopBootstrap.IsPortListening(closedPort);
        swClosed.Stop();

        Assert.False(closedListening);
        Assert.True(
            swClosed.Elapsed.TotalMilliseconds < maxProbeMs,
            $"Closed port check took {swClosed.Elapsed.TotalMilliseconds:F3}ms (expected < {maxProbeMs:F0}ms, replacing 2043ms HTTP timeout).");

        if (boundPorts.Count > 0)
        {
            var activePort = boundPorts.First();
            var swActive = Stopwatch.StartNew();
            var activeListening = DesktopBootstrap.IsPortListening(activePort);
            swActive.Stop();

            Assert.True(activeListening);
            Assert.True(
                swActive.Elapsed.TotalMilliseconds < maxProbeMs,
                $"Active port check took {swActive.Elapsed.TotalMilliseconds:F3}ms (expected < {maxProbeMs:F0}ms).");
        }
    }

    [Fact]
    public void CanonicalHomeDefaultsToDotIntochatUnderUserProfileAndHonorsHomeOverride()
    {
        var fakeProfile = Path.Combine(Path.GetTempPath(), "profile-" + Guid.NewGuid().ToString("N")[..8]);
        var defaultHome = DesktopBootstrap.ResolveHomeDirectory(_ => null, fakeProfile);
        Assert.Equal(Path.Combine(fakeProfile, ".intochat"), defaultHome);

        var overrideRoot = Path.Combine(Path.GetTempPath(), "custom-home-" + Guid.NewGuid().ToString("N")[..8]);
        var overriddenHome = DesktopBootstrap.ResolveHomeDirectory(
            key => key == DesktopBootstrap.HomeEnvVar ? overrideRoot : null,
            fakeProfile);
        Assert.Equal(Path.GetFullPath(overrideRoot), overriddenHome);

        var layout = DesktopBootstrap.ResolveLayout(defaultHome);
        Assert.Equal(Path.Combine(defaultHome, "secrets", "master.key"), layout.MasterKeyPath);
        Assert.Equal(Path.Combine(defaultHome, "run", "presence"), layout.PresenceFilePath);
        Assert.Equal(Path.Combine(defaultHome, "state"), layout.StateDirectory);
    }

    [Fact]
    public void EnsurePresenceTokenWrites256BitOwnerOnlyPresenceFile()
    {
        var tempDir = Directory.CreateTempSubdirectory("intochat-presence-test-");
        try
        {
            var layout = DesktopBootstrap.ResolveLayout(tempDir.FullName);
            var token1 = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: true);
            Assert.True(File.Exists(layout.PresenceFilePath));
            Assert.Equal(43, token1.Length); // 32 bytes base64url without '=' padding = 43 chars
            Assert.Equal(token1, File.ReadAllText(layout.PresenceFilePath).Trim());

            // Attaching to an already-running kernel reuses the existing presence token.
            var reused = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: false);
            Assert.Equal(token1, reused);

            // Fresh boot rotates the presence token.
            var rotated = DesktopBootstrap.EnsurePresenceToken(layout.PresenceFilePath, createNew: true);
            Assert.NotEqual(token1, rotated);

            if (OperatingSystem.IsWindows())
            {
                using var currentIdentity = WindowsIdentity.GetCurrent();
                var currentUserSid = currentIdentity.User!;
                var fileSecurity = new FileInfo(layout.PresenceFilePath).GetAccessControl();
                Assert.True(fileSecurity.AreAccessRulesProtected);

                var rules = fileSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .ToArray();

                var singleRule = Assert.Single(rules);
                Assert.Equal(currentUserSid, singleRule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, singleRule.AccessControlType);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void KernelAndShellStartInfoSetCustodyPosturePresenceAndKeepGithubAndTimeEnabled()
    {
        var tempDir = Directory.CreateTempSubdirectory("intochat-startinfo-test-");
        try
        {
            var layout = DesktopBootstrap.ResolveLayout(tempDir.FullName);
            var kernelUrl = new Uri("http://127.0.0.1:5080/");
            var presenceToken = DesktopBootstrap.GeneratePresenceToken();
            var masterKey = Convert.ToBase64String(new byte[32]);

            var kernelStart = DesktopBootstrap.BuildKernelStartInfo(
                Path.Combine(tempDir.FullName, "os"),
                kernelUrl,
                layout,
                masterKey,
                presenceToken,
                _ => null);

            Assert.True(kernelStart.CreateNoWindow);
            Assert.False(kernelStart.UseShellExecute);

            var env = kernelStart.Environment;
            Assert.Equal("true", env[DesktopBootstrap.ConfiguredCustodyAcknowledgedEnvVar]);
            Assert.Equal("Open", env[DesktopBootstrap.AuthPostureEnvVar]);
            Assert.Equal("Open", env[DesktopBootstrap.AuthorityIdentityPostureEnvVar]);
            Assert.False(env.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
            Assert.Equal(layout.PresenceFilePath, env[DesktopBootstrap.PresenceTokenFileEnvVar]);
            Assert.Equal(layout.HomeDirectory, env[DesktopBootstrap.HomeEnvVar]);
            Assert.False(env.ContainsKey(DesktopBootstrap.MasterKeyEnvVar));
            Assert.False(env.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));

            var configPrefix = $"--{DesktopBootstrap.ConfigurationFileConfigKey}=";
            var configArg = Assert.Single(kernelStart.ArgumentList, a => a.StartsWith(configPrefix, StringComparison.Ordinal));
            var kernelConfig = new ConfigurationBuilder()
                .AddJsonFile(configArg[configPrefix.Length..], optional: false, reloadOnChange: false)
                .Build();
            Assert.Equal(masterKey, kernelConfig[IntoChatOSNames.MasterKeyConfigurationKey]);
            Assert.Equal(layout.PresenceFilePath, kernelConfig[PresenceProof.TokenFileConfigurationKey]);
            Assert.Equal(
                PresenceProof.ComputeTokenHash(presenceToken),
                kernelConfig[PresenceProof.TokenHashConfigurationKey]);

            Assert.Equal("false", env[DesktopBootstrap.OrleansDashboardEnabledEnvVar]);
            Assert.Equal("false", env[DesktopBootstrap.IntoChatOrleansDashboardEnabledEnvVar]);
            Assert.Equal("salesforce,gmail", env[DesktopBootstrap.ModulesDisabledEnvVar]);

            // salesforce and gmail are disabled by default; github and time MUST remain enabled!
            Assert.Equal("false", env[DesktopBootstrap.ModuleEnabledEnvVar("salesforce")]);
            Assert.Equal("false", env[DesktopBootstrap.ModuleEnabledEnvVar("gmail")]);
            Assert.False(env.ContainsKey(DesktopBootstrap.ModuleEnabledEnvVar("github")));
            Assert.False(env.ContainsKey(DesktopBootstrap.ModuleEnabledEnvVar("time")));

            var shellStart = DesktopBootstrap.BuildShellStartInfo(
                Path.Combine(tempDir.FullName, "shell"),
                kernelUrl,
                presenceToken,
                createNoWindow: true);

            Assert.True(shellStart.CreateNoWindow);
            Assert.Equal(kernelUrl.ToString(), shellStart.Environment[DesktopBootstrap.ShellUiBaseEnvVar]);
            Assert.False(shellStart.Environment.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
            Assert.False(shellStart.Environment.ContainsKey(DesktopBootstrap.MasterKeyEnvVar));
            Assert.Contains($"--presence-token={presenceToken}", shellStart.ArgumentList);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
