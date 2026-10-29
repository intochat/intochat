using DigitalBrain.Testing;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class SettingsSideloadFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> Operations
        => AppBaseline.Cases(typeof(SettingsApp)).Where(row => row.TestDisplayName!.Contains("sideload", StringComparison.OrdinalIgnoreCase));

    [Theory, MemberData(nameof(Operations))]
    public Task Baseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task TheAppsRootIsTheSideloadDirectory()
    {
        var root = Directory.CreateTempSubdirectory("apps-root-").FullName;
        try
        {
            var disk = new Disk();
            var full = AppsDirectoryRoot.Prepare(root);
            await AppsDirectoryRoot.Allow(disk, root);
            Assert.Equal(AppsDirectoryRoot.Label, "Apps");
            Assert.Equal(full, disk.Allowed);
            Assert.True(Directory.Exists(full));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Disk : ILocalDisk
    {
        public string? Allowed { get; private set; }
        public Task<string[]> Roots() => Task.FromResult(Allowed is null ? [] : new[] { Allowed });
        public Task AllowRoot(string path) { Allowed = path; return Task.CompletedTask; }
        public Task RevokeRoot(string path) => Task.CompletedTask;
    }
}
