using System.Text.Json;
using System.Threading.Channels;

namespace IntoChat.OS.Cli.Tests.Unit;

public sealed class FolderRunFacts
{
    [Fact]
    public async Task ThreeWritesInsideTheQuietWindowPushOnce()
    {
        var pulses = Channel.CreateBounded<byte>(new BoundedChannelOptions(8) { SingleReader = true });
        var pushes = 0;
        var done = FolderRun.Coalesce(pulses.Reader, TimeSpan.FromMilliseconds(40), () =>
        {
            pushes++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        pulses.Writer.TryWrite(1);
        pulses.Writer.TryWrite(1);
        pulses.Writer.TryWrite(1);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        pulses.Writer.TryComplete();
        await done;
        Assert.Equal(1, pushes);
    }

    [Fact]
    public void TheAppNameComesFromTheDeclaration()
        => Assert.Equal("notes", FolderRun.AppName("public const string Name = \"notes\";\n"));

    [Fact]
    public void ThePushRoundTripsThroughCliJsonContext()
    {
        var push = new DevAppPush([new DevAppFile("app.cs", "const string Name = \"notes\";")]);
        var json = JsonSerializer.Serialize(push, CliJsonContext.Default.DevAppPush);
        Assert.Contains("\"files\"", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(json, CliJsonContext.Default.DevAppPush);
        Assert.Equal("app.cs", Assert.Single(back!.Files).Path);
    }

    [Fact]
    public async Task ATokenArgumentIsRefused()
    {
        var code = await Program.Main(["run", "folder", "--token", "secret"]);
        Assert.Equal(2, code);
    }
}
