namespace IntoChat.OS.LocalSetup.Tests.Unit;

public sealed class LauncherCommandFacts
{
    [Theory]
    [InlineData("local", LauncherKind.Local)]
    [InlineData("preflight", LauncherKind.Preflight)]
    [InlineData("reset", LauncherKind.Reset)]
    [InlineData("headless", LauncherKind.Headless)]
    [InlineData("custom.json", LauncherKind.ConfigFile)]
    [InlineData("CUSTOM.JSON", LauncherKind.ConfigFile)]
    [InlineData("default", LauncherKind.Default)]
    [InlineData("bogus", LauncherKind.Unknown)]
    public void ProfileWordSelectsKind(string word, LauncherKind kind)
    {
        var command = LauncherCommand.Parse([word]);
        Assert.Equal(kind, command.Kind);
        Assert.Equal(kind == LauncherKind.Unknown ? 2 : null, command.ImmediateExitCode);
    }

    [Fact]
    public void NoArgumentsAndFlagsOnlyAreTheDefaultProfile()
    {
        Assert.Equal(LauncherKind.Default, LauncherCommand.Parse([]).Kind);
        var flagsOnly = LauncherCommand.Parse(["--json"]);
        Assert.Equal(LauncherKind.Default, flagsOnly.Kind);
        Assert.True(flagsOnly.Json);
    }

    [Fact]
    public void HeadlessProjectsShellDisabledAndJsonProjectsConfigFile()
    {
        Assert.Equal(["--IntoChatOS:Shell:Enabled=false"], LauncherCommand.Parse(["headless"]).ProfileArguments());
        var json = LauncherCommand.Parse(["custom.json"]).ProfileArguments();
        Assert.Equal("--IntoChatOS:ConfigFile=" + Path.GetFullPath("custom.json"), Assert.Single(json));
        Assert.Empty(LauncherCommand.Parse(["default"]).ProfileArguments());
    }

    [Fact]
    public void UnknownFlagsAndArgumentsAfterSeparatorForwardToTheAppHost()
    {
        var command = LauncherCommand.Parse(["headless", "--Foo=1", "--", "--Bar=2", "--json"]);
        Assert.Equal(["--Foo=1", "--Bar=2", "--json"], command.Remaining);
        Assert.False(command.Json);
        Assert.Equal(["--IntoChatOS:Shell:Enabled=false", "--Foo=1", "--Bar=2", "--json"], command.AppHostArguments());
    }

    [Fact]
    public void LauncherFlagsAreConsumed()
    {
        var command = LauncherCommand.Parse(["local", "--skip-preflight", "--pull", "--no-fail", "--allow-registry", "--fake", "m.json", "--model", "qwen3.5:2b", "--endpoint", "http://h:1"]);
        Assert.True(command.SkipPreflight);
        Assert.True(command.Pull);
        Assert.True(command.NoFail);
        Assert.True(command.AllowRegistry);
        Assert.Equal("m.json", command.Fake);
        Assert.Equal("qwen3.5:2b", command.Model);
        Assert.Equal("http://h:1", command.Endpoint);
        Assert.Empty(command.Remaining);
    }

    [Fact]
    public void FlagMissingItsValueIsAUsageError()
    {
        var command = LauncherCommand.Parse(["preflight", "--fake"]);
        Assert.Equal(2, command.ImmediateExitCode);
        Assert.Contains("--fake", command.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, new[] { "--IntoChatOS:Topology=local", "--IntoChatOS:Local:Gpu=true" })]
    [InlineData(false, true, new[] { "--IntoChatOS:Topology=local", "--IntoChatOS:Local:Gpu=false" })]
    [InlineData(true, false, new[] { "--IntoChatOS:Topology=local", "--IntoChatOS:Local:Gpu=true", "--IntoChatOS:Shell:Enabled=false" })]
    public void LocalAppHostArgumentsCarryGpuAndShell(bool gpu, bool shell, string[] expected)
        => Assert.Equal(expected, LauncherCommand.Parse(["local"]).LocalAppHostArguments(gpu, shell));

    [Fact]
    public void LocalAppHostArgumentsCarryModelOverrideAndForwardedArguments()
    {
        var arguments = LauncherCommand.Parse(["local", "--model", "qwen3.5:2b", "--", "--X=1"]).LocalAppHostArguments(false, true);
        Assert.Contains("--IntoChatOS:Local:Model=qwen3.5:2b", arguments);
        Assert.Equal("--X=1", arguments[^1]);
    }
}
