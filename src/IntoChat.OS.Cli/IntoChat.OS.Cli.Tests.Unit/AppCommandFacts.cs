namespace IntoChat.OS.Cli.Tests.Unit;

public sealed class AppCommandFacts
{
    [Fact]
    public void RunPreservesTheProjectPathAndExplicitDestination()
    {
        var command = AppCommand.Parse(["run", "C:/My App/MyApp.csproj", "--endpoint", "http://localhost:5080/", "--brain", "work"]);
        Assert.Equal(AppCommandKind.Run, command.Kind);
        Assert.Equal("C:/My App/MyApp.csproj", command.Target);
        Assert.Equal(new Uri("http://localhost:5080/"), command.Endpoint);
        Assert.Equal("work", command.Brain);
        Assert.Null(command.Output);
    }

    [Fact]
    public void PackRequiresAndPreservesItsOutputFile()
    {
        var command = AppCommand.Parse(["pack", "./app.csproj", "-o", "./dist/app.intochat-app"]);
        Assert.Equal(AppCommandKind.Pack, command.Kind);
        Assert.Equal("./app.csproj", command.Target);
        Assert.Equal("./dist/app.intochat-app", command.Output);
        Assert.Null(command.Endpoint);
    }

    [Fact]
    public void InstallSelectsAnArtifactWithoutInferringAProject()
    {
        var command = AppCommand.Parse(["install", "app.intochat-app"]);
        Assert.Equal(AppCommandKind.Install, command.Kind);
        Assert.Equal("app.intochat-app", command.Target);
        Assert.Null(command.Brain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("help")]
    public void HelpDoesNotNeedAProject(string argument) => Assert.Equal(AppCommandKind.Help, AppCommand.Parse(argument.Length == 0 ? [] : [argument]).Kind);

    [Theory]
    [InlineData("delete|app")]
    [InlineData("run")]
    [InlineData("pack|app.csproj")]
    [InlineData("pack|app.csproj|-o")]
    [InlineData("run|app.csproj|--brain")]
    [InlineData("run|one.csproj|two.csproj")]
    [InlineData("run|app.csproj|--unknown|value")]
    [InlineData("run|app.csproj|--brain|one|--brain|two")]
    [InlineData("run|app.csproj|-o|output")]
    [InlineData("pack|app.csproj|-o|output|--brain|work")]
    [InlineData("install|app.intochat-app|--endpoint|file:///tmp/host")]
    [InlineData("install|app.intochat-app|--endpoint|https://user:password@host/")]
    [InlineData("run| |--brain|work")]
    public void InvalidInvocationFailsBeforeAnyBuildOrInstall(string arguments)
        => Assert.Throws<ArgumentException>(() => AppCommand.Parse(arguments.Split('|')));
}
