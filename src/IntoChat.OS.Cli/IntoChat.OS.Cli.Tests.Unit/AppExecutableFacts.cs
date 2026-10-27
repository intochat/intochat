namespace IntoChat.OS.Cli.Tests.Unit;

public sealed class AppExecutableFacts
{
    [Fact]
    public async Task HelpAndUnfinishedCommandsHaveHonestExitCodes()
    {
        var cli = typeof(AppBuild).Assembly.Location;
        var process = new BuildProcess();
        var help = await process.Run(AppContext.BaseDirectory, [cli, "help"], TestContext.Current.CancellationToken);
        Assert.Contains("intochat pack", help, StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => process.Run(AppContext.BaseDirectory, [cli, "run", "not-a-project.csproj"], TestContext.Current.CancellationToken));
        Assert.Contains("not implemented", error.Message, StringComparison.Ordinal);
    }
}
