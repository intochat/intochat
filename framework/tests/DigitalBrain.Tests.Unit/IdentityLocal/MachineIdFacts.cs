using DigitalBrain.Identity.Local.Machine;

namespace DigitalBrain.Identity.Local.Tests.Unit;

public sealed class MachineIdFacts : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "db-machine-id-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_root)) { System.IO.Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void TheIdIsCreatedOnceThenReadBack()
    {
        var file = Path.Combine(_root, "state", "machine-id");

        var created = new MachineId(file).Value;

        Assert.True(Guid.TryParseExact(created, "N", out _));
        Assert.Equal(created, new MachineId(file).Value);
        Assert.Equal(created, File.ReadAllText(file));
    }

    [Fact]
    public void AGarbledFileIsAnErrorAndIsNeverReplaced()
    {
        System.IO.Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "machine-id");
        File.WriteAllText(file, "not-a-guid");

        Assert.Throws<InvalidDataException>(() => new MachineId(file).Value);
        Assert.Equal("not-a-guid", File.ReadAllText(file));
    }

    [Fact]
    public void NothingIsWrittenUntilTheIdIsRead()
    {
        var file = Path.Combine(_root, "machine-id");

        _ = new MachineId(file);

        Assert.False(File.Exists(file));
    }
}
