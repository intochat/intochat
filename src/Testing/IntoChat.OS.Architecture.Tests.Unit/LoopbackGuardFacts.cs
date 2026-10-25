namespace IntoChat.OS.Architecture.Tests.Unit;

// The Open-posture loopback guard follows the topology (D-21): it reads BindPolicy, which reads IntoChatOS:Topology and
// IntoChatOS:Bind through the shared constants. The run-mode marker it used to read is retired; its key lives only in the
// retired table of LegacyConfigurationGuard (RetiredKeyFacts), so the guard can no longer be disabled by renaming a marker.
public sealed class LoopbackGuardFacts
{
    [Fact]
    public void TheTopologyAndBindKeysKeepTheirLiterals()
    {
        Assert.Equal("IntoChatOS:Topology", IntoChat.OS.Framework.IntoChatOSNames.TopologyKey);
        Assert.Equal("IntoChatOS:Bind", IntoChat.OS.Framework.IntoChatOSNames.BindKey);
        Assert.Equal("local", IntoChat.OS.Framework.IntoChatOSNames.TopologyLocal);
    }

    [Fact]
    public void TheLoopbackGuardReadsTheBindPolicy()
    {
        var guard = File.ReadAllText(Assert.Single(Repository.ProjectFiles("OpenPostureLoopback.cs").ToArray()));
        Assert.Contains("BindPolicy.IsLocal(configuration)", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("IsRunMode", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("Hosting:", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("HostingMode", guard, StringComparison.Ordinal);
        var policy = File.ReadAllText(Assert.Single(Repository.ProjectFiles("BindPolicy.cs").ToArray()));
        Assert.Contains("IntoChatOSNames.TopologyKey", policy, StringComparison.Ordinal);
        Assert.Contains("IntoChatOSNames.BindKey", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceDefaultsApplyTheBindPolicyToEveryServer()
    {
        var defaults = File.ReadAllText(Assert.Single(Repository.ProjectFiles("ServiceDefaultsExtensions.cs").ToArray()));
        Assert.Contains("OpenPostureLoopback.Validate(builder.Configuration)", defaults, StringComparison.Ordinal);
        Assert.Contains("BindPolicyKestrel.Validate(builder.Configuration)", defaults, StringComparison.Ordinal);
    }
}
