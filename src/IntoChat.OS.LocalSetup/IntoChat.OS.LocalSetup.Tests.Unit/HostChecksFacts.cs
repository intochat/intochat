namespace IntoChat.OS.LocalSetup.Tests.Unit;

public sealed class HostChecksFacts
{
    [Theory]
    [InlineData("all-green", "docker-engine", CheckStatus.Pass, null)]
    [InlineData("all-green", "linux-containers", CheckStatus.Pass, null)]
    [InlineData("all-green", "docker-memory", CheckStatus.Pass, null)]
    [InlineData("all-green", "wsl", CheckStatus.Pass, null)]
    [InlineData("all-green", "disk-repo", CheckStatus.Pass, null)]
    [InlineData("all-green", "disk-models", CheckStatus.Pass, null)]
    [InlineData("all-green", "ports", CheckStatus.Pass, null)]
    [InlineData("docker-windows-containers", "linux-containers", CheckStatus.Fail, "Switch Docker Desktop to Linux containers.")]
    [InlineData("docker-down", "docker-engine", CheckStatus.Fail, "Start Docker Desktop")]
    [InlineData("low-memory", "docker-memory", CheckStatus.Warn, "at least 6 GiB")]
    [InlineData("low-disk", "disk-repo", CheckStatus.Fail, "about 20 GiB")]
    [InlineData("low-disk", "disk-models", CheckStatus.Fail, "about 20 GiB")]
    [InlineData("port-taken", "port-5080", CheckStatus.Fail, "Stop the process using port 5080")]
    [InlineData("port-excluded", "port-15100", CheckStatus.Fail, "excludedportrange")]
    public async Task FixtureYieldsStatusAndFixLine(string fixture, string id, CheckStatus status, string? fixContains)
    {
        var result = Fixtures.Result(await Fixtures.CollectAsync(fixture), id);
        Assert.Equal(status, result.Status);
        if (fixContains is null) { Assert.Null(result.Fix); }
        else { Assert.Contains(fixContains, result.Fix, StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData("all-green", 0)]
    [InlineData("low-memory", 0)]
    [InlineData("docker-windows-containers", 1)]
    [InlineData("docker-down", 1)]
    [InlineData("low-disk", 1)]
    [InlineData("port-taken", 1)]
    [InlineData("port-excluded", 1)]
    public async Task ExitCodeIsZeroForWarningsAndOneForFailures(string fixture, int exit)
        => Assert.Equal(exit, (await Fixtures.RunAsync(fixture)).Exit);

    [Fact]
    public async Task WindowsContainersPrintTheFixLineAndFailTheRun()
    {
        var (exit, output) = await Fixtures.RunAsync("docker-windows-containers");
        Assert.Equal(1, exit);
        Assert.Contains("[FAIL] linux-containers", output, StringComparison.Ordinal);
        Assert.Contains("Switch Docker Desktop to Linux containers", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoFailForcesExitZero()
        => Assert.Equal(0, (await Fixtures.RunAsync("docker-down", "--no-fail")).Exit);

    [Fact]
    public async Task DockerDownSkipsDependentChecks()
    {
        var report = await Fixtures.CollectAsync("docker-down");
        Assert.DoesNotContain(report.Results, result => result.Id is "linux-containers" or "docker-memory");
    }

    [Fact]
    public async Task WslOutputIsDecodedAsUtf16()
    {
        var wsl = Fixtures.Result(await Fixtures.CollectAsync("wsl-utf16"), "wsl");
        Assert.Contains("Ubuntu-24.04", wsl.Message, StringComparison.Ordinal);
        Assert.Contains("docker-desktop", wsl.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDistrosHandlesBomAndDefaultMarker()
    {
        var bytes = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("  NAME STATE VERSION\r\n* Debian Stopped 2\r\n")).ToArray();
        Assert.Equal(["Debian"], Checks.WslCheck.ParseDistros(bytes));
    }

    [Fact]
    public async Task WslIsSkippedOffWindows()
    {
        var environment = FakeProbeEnvironment.FromJson("""{ "os": "linux", "freeBytes": { "*": 100000000000 } }""");
        var report = await Preflight.CollectAsync(environment, new PreflightOptions(new LocalModelOptions(), false, "repo"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(report.Results, result => result.Id == "wsl");
    }

    [Fact]
    public async Task ShellPortsAreCheckedOnlyWhenTheShellIsEnabled()
    {
        var json = """{ "os": "linux", "boundPorts": [54721], "freeBytes": { "*": 100000000000 } }""";
        var with = await Preflight.CollectAsync(FakeProbeEnvironment.FromJson(json), new PreflightOptions(new LocalModelOptions(), true, "repo"), TestContext.Current.CancellationToken);
        var without = await Preflight.CollectAsync(FakeProbeEnvironment.FromJson(json), new PreflightOptions(new LocalModelOptions(), false, "repo"), TestContext.Current.CancellationToken);
        Assert.Contains(with.Results, result => result.Id == "port-54721" && result.Status == CheckStatus.Fail);
        Assert.DoesNotContain(without.Results, result => result.Id == "port-54721");
    }

    [Fact]
    public async Task JsonOutputCarriesStatusesAndNoEnvironmentValues()
    {
        var (_, output) = await Fixtures.RunAsync("all-green", "--json");
        using var document = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains(document.RootElement.GetProperty("results").EnumerateArray(), item => item.GetProperty("status").GetString() == "PASS");
    }

    [Fact]
    public async Task EnvironmentValuesNeverAppearInOutput()
    {
        var environment = FakeProbeEnvironment.FromJson("""{ "os": "windows", "env": { "OLLAMA_MODELS": "D:\\secret-token-123" }, "freeBytes": { "*": 100000000000 } }""");
        var report = await Preflight.CollectAsync(environment, new PreflightOptions(new LocalModelOptions(), true, "repo"), TestContext.Current.CancellationToken);
        using var writer = new StringWriter();
        PreflightRenderer.Render(report, report.ExitCode(), json: false, writer);
        Assert.DoesNotContain("secret-token-123", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageErrorsExitTwo()
    {
        using var writer = new StringWriter();
        Assert.Equal(2, await Preflight.RunAsync(["preflight", "--bogus"], writer, TestContext.Current.CancellationToken));
        Assert.Equal(2, await Preflight.RunAsync(["local"], writer, TestContext.Current.CancellationToken));
        Assert.Equal(2, await Preflight.RunAsync(["preflight", "--fake", "missing-fixture.json"], writer, TestContext.Current.CancellationToken));
    }

}
