using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A test host that binds a literal port collides with whatever else runs on the machine, and with the other test modules
// CI runs in parallel. HTTP hosts bind port 0 and read the bound address back; the Orleans silo and gateway, which
// refuse port 0, take theirs from PortReservation. A literal non-zero bind in a PR-gate test project fails here.
public sealed partial class TestPortFacts
{
    [GeneratedRegex("""UseUrls\(\s*\$?"[^"]*:(?<port>\d+)[/"]""")]
    private static partial Regex UseUrlsBind();

    [GeneratedRegex(@"new\s+Tcp" + @"Listener\([^,()]+,\s*(?<port>\d+)\s*\)")]
    private static partial Regex ListenerBind();

    [GeneratedRegex(@"Listen(?:Localhost|Loopback|AnyIP)?\(\s*(?:IPAddress\.\w+\s*,\s*)?(?<port>\d+)\s*[,)]")]
    private static partial Regex KestrelBind();

    [GeneratedRegex(@"(?:SiloPort|GatewayPort)\s*=\s*(?<port>\d+)\b")]
    private static partial Regex OrleansPort();

    [Fact]
    public void NoPullRequestGateTestBindsALiteralPort()
    {
        string[] gate = [".Tests.Unit", ".Tests.Integration", ".Tests.Hosting"];
        Regex[] binds = [UseUrlsBind(), ListenerBind(), KestrelBind(), OrleansPort()];
        var problems = new List<string>();
        foreach (var project in Repository.TestProjects().Where(project => gate.Any(tier => project.Name.EndsWith(tier, StringComparison.Ordinal))))
        {
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs", path => !path.EndsWith("TestPortFacts.cs", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(file);
                foreach (var bind in binds)
                {
                    foreach (var match in bind.Matches(text).Where(match => match.Groups["port"].Value.Trim('0').Length > 0))
                    {
                        problems.Add($"{Repository.Relative(file)}: {match.Value.Trim()}");
                    }
                }
            }
        }
        Assert.True(problems.Count == 0, "Bind port 0 (HTTP) or reserve through PortReservation (Orleans):\n" + string.Join('\n', problems));
    }

    [Theory]
    [InlineData("""builder.WebHost.UseUrls("http://127.0.0.1:8080");""", true)]
    [InlineData("""builder.WebHost.UseUrls($"http://127.0.0.1:5000/");""", true)]
    [InlineData("""builder.WebHost.UseUrls("http://127.0.0.1:0");""", false)]
    [InlineData("""builder.WebHost.UseUrls($"http://127.0.0.1:{port}");""", false)]
    [InlineData("new Tcp" + "Listener(IPAddress.Loopback, 9000)", true)]
    [InlineData("new Tcp" + "Listener(IPAddress.Loopback, 0)", false)]
    [InlineData("""options.ListenLocalhost(7000);""", true)]
    [InlineData("""options.ListenLocalhost(0);""", false)]
    [InlineData("""SiloPort = 11111""", true)]
    [InlineData("""Assert.Equal("http://127.0.0.1:8080", url);""", false)]
    public void TheBindPatternsFlagLiteralPortsOnly(string source, bool flagged)
    {
        var hit = new[] { UseUrlsBind(), ListenerBind(), KestrelBind(), OrleansPort() }
            .Any(bind => bind.Matches(source).Any(match => match.Groups["port"].Value.Trim('0').Length > 0));
        Assert.Equal(flagged, hit);
    }
}
