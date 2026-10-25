namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

// Runs on every OS: root containment follows the host's path case rules.
public sealed class MachineRootsFacts
{
    [Fact]
    public void ContainmentFollowsTheHostsPathCaseRules()
    {
        var root = MachineRoots.Normalize(Path.Combine(Path.GetTempPath(), "MachineRoots"));
        var differentCase = Path.Combine(root.ToLowerInvariant(), "notes.txt");

        Assert.Equal(Path.Combine(root, "notes.txt"), MachineRoots.Resolve([root], "notes.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => MachineRoots.Resolve([root], root + "2"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(differentCase, MachineRoots.Resolve([root], differentCase));
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => MachineRoots.Resolve([root], differentCase));
        }
    }
}
