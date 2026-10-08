using DigitalBrain.Framework;
using DigitalBrain.Machine;

namespace DigitalBrain.Modules.Machine.Tests;

// The machine edge: a shell command runs on the host, files are read and written within the root.
public sealed class MachineFacts
{
    private const string Principal = "acct";
    private const string BrainName = "home";
    private static readonly string Scope = BrainScope.Create(Principal, BrainName).Id;

    [Fact(Timeout = 60_000)]
    public async Task ACommandRunsOnTheHostAndItsOutputIsReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Path.GetTempPath(), ct);
        Stamp();
        await host.AuthorizeCallerAsync();

        var result = await host.Get<IConsole>(Scope).Run("echo hello", null, 30, ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 60_000)]
    public async Task FilesAreWrittenAndReadWithinTheRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("machine-facts").FullName;
        await using var host = await StartAsync(root, ct);
        Stamp();
        await host.AuthorizeCallerAsync();

        var files = host.Get<IFileSystem>(Scope);
        await files.Write("note.txt", "hi there", ct);

        Assert.Equal("hi there", await files.Read("note.txt", ct));
        Assert.Contains(await files.List(".", ct), entry => entry.EndsWith("note.txt", StringComparison.Ordinal));
    }

    [Fact(Timeout = 60_000)]
    public async Task APathOutsideTheRootIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("machine-facts").FullName;
        await using var host = await StartAsync(root, ct);
        Stamp();
        await host.AuthorizeCallerAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Get<IFileSystem>(Scope).Read("../escape.txt", ct));
    }

    private static Task<ModuleBrain> StartAsync(string root, CancellationToken ct) => ModuleTest.Create()
        .WithModule<MachineModule>()
        .ConfigureModule<MachineModule, MachineOptions>(options =>
        {
            options.Enabled = true;
            options.Root = root;
        })
        .StartAsync(ct);

    private static void Stamp() => CallerContextStamper.Stamp(new()
    { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
}
