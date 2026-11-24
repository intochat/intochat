using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Install;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

// The operator's verb for the cloud's one-time record move: "move-records <plan.json> <phase>". The move itself is proven in the
// framework (BrainRecordMoveFacts); here, the verb's arguments, its refusals and its phase order.
public sealed class RecordMoveCommandFacts : IDisposable
{
    private readonly TempDirectory root = new("record-move-command-");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IConfiguration Keys = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["IntoChatOS:MasterKey"] = "test-master-key-0123456789",
    }).Build();

    public void Dispose() => root.Dispose();

    private async Task<string> PlanFile(string json)
    {
        var file = Path.Combine(root.Path, "plan.json");
        await File.WriteAllTextAsync(file, json, Ct);
        return file;
    }

    private string Plan(string backup = "backup") => $$"""
        {
          "sourceHome": "{{Path.Combine(root.Path, "os").Replace("\\", "\\\\", StringComparison.Ordinal)}}",
          "targetHome": "{{Path.Combine(root.Path, "cloud").Replace("\\", "\\\\", StringComparison.Ordinal)}}",
          "backup": "{{Path.Combine(root.Path, backup).Replace("\\", "\\\\", StringComparison.Ordinal)}}",
          "brains": ["system", "ada"],
          "rules": [{ "neuron": "lobby.visitor" }, { "grain": "circle-server.machine-credential/" }]
        }
        """;

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await RecordMoveCommand.Run(args, Keys, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void OnlyItsVerbIsAMove()
    {
        Assert.True(RecordMoveCommand.IsMove(["move-records", "plan.json", "plan"]));
        Assert.False(RecordMoveCommand.IsMove(["install"]));
        Assert.False(RecordMoveCommand.IsMove([]));
    }

    [Theory]
    [InlineData("move-records")]
    [InlineData("move-records", "plan.json")]
    [InlineData("move-records", "plan.json", "delete-everything")]
    public async Task AnUnknownPhaseOrAMissingPlanIsAUsageError(params string[] args)
        => Assert.Equal(64, (await Run(args)).Code);

    [Fact]
    public async Task AnUnreadablePlanIsRefused()
    {
        Assert.Equal(66, (await Run("move-records", Path.Combine(root.Path, "absent.json"), "plan")).Code);
        Assert.Equal(66, (await Run("move-records", await PlanFile("{ not json"), "plan")).Code);
    }

    [Fact]
    public async Task ThePhasesRunInOrder()
    {
        var file = await PlanFile(Plan());

        var early = await Run("move-records", file, "copy");
        Assert.Equal(1, early.Code);
        Assert.Contains("Backup phase first", early.Error, StringComparison.Ordinal);

        Assert.Equal(0, (await Run("move-records", file, "plan")).Code);
        Assert.Equal(0, (await Run("move-records", file, "backup")).Code);
        var copy = await Run("move-records", file, "copy");
        Assert.Equal(0, copy.Code);
        Assert.Contains("copy ada: selected 0", copy.Output, StringComparison.Ordinal);
        Assert.Equal(0, (await Run("move-records", file, "remove-source")).Code);
    }

    [Fact]
    public async Task ABackupInsideAHomeIsRefused()
    {
        var file = await PlanFile(Plan(backup: Path.Combine("os", "backup")));

        var refused = await Run("move-records", file, "backup");

        Assert.Equal(1, refused.Code);
        Assert.Contains("outside both Homes", refused.Error, StringComparison.Ordinal);
    }
}
